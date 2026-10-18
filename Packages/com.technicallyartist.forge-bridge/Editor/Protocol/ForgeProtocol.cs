using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ForgeBridge.Editor
{
    public sealed class PushResult
    {
        public string AssetPath = "";
        public bool Created;
        public List<string> Warnings = new List<string>();
        public string Error;

        public static PushResult Failure(string error) => new PushResult { Error = error };
    }

    public sealed class BridgeProtocolException : Exception
    {
        public ushort CloseCode { get; }
        public BridgeProtocolException(ushort closeCode, string message) : base(message) { CloseCode = closeCode; }
    }

    public sealed class ForgeMessage
    {
        public JObject Header { get; }
        public byte[] Payload { get; }
        public ForgeMessage(JObject header, byte[] payload) { Header = header; Payload = payload; }
    }

    public static class ForgeProtocol
    {
        public const string Version = "1.2";
        public const int HeaderLimit = 64 * 1024;
        public const int MaximumPayload = 256 * 1024 * 1024;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static bool AcceptsVersion(string version)
        {
            if (version == null) return false;
            var parts = version.Split('.');
            return parts.Length == 2 && IsDigits(parts[0]) && IsDigits(parts[1]) &&
                int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) && major == 1 && minor <= 1;
        }

        private static bool IsDigits(string text)
        {
            if (text.Length == 0) return false;
            foreach (var c in text) if (c < '0' || c > '9') return false;
            return true;
        }

        public static string String(JObject header, string path)
        {
            var token = header.SelectToken(path, false);
            return token?.Type == JTokenType.String ? (string)token : null;
        }

        public static JObject Header(string type, string reference = null)
        {
            var header = new JObject { ["protocol"] = Version, ["type"] = type, ["id"] = Guid.NewGuid().ToString("D") };
            if (reference != null) header["ref"] = reference;
            return header;
        }

        public static JObject Welcome(string reference, string projectId, string projectName, string editorVersion, string pluginVersion)
        {
            var header = Header("welcome", reference);
            header["plugin"] = new JObject { ["version"] = pluginVersion };
            header["engine"] = new JObject { ["name"] = "unity", ["version"] = editorVersion };
            header["project"] = new JObject { ["name"] = projectName };
            header["projectId"] = projectId;
            header["projectName"] = projectName;
            header["editorVersion"] = editorVersion;
            return header;
        }

        public static JObject Denied(string reason, string message, string reference = null)
        {
            var header = Header("denied", reference);
            header["reason"] = reason;
            header["message"] = message;
            if (reason == "protocol") header["supported"] = Version;
            return header;
        }

        public static JObject Result(string reference, PushResult result)
        {
            var header = Header("result", reference);
            header["ok"] = string.IsNullOrEmpty(result.Error);
            header["assetPath"] = result.AssetPath ?? "";
            header["created"] = result.Created;
            var warnings = new JArray();
            header["warnings"] = warnings;
            bool truncated = false;
            const string notice = "Additional diagnostics were truncated; see the Unity Console for the full messages.";
            int noticeBytes = Utf8.GetByteCount(JsonConvert.SerializeObject(notice)) + 1;
            if (!string.IsNullOrEmpty(result.Error)) header["error"] = LimitDiagnostic(result.Error, 8192, ref truncated);
            int bytes = Utf8.GetByteCount(header.ToString(Formatting.None));
            // A large correlation id/path may leave little room for the failure text. Preserve
            // correlation and outcome first; the complete diagnostics are already in the Console.
            while (bytes + noticeBytes > HeaderLimit && header["error"] is JValue error && ((string)error).Length > 128)
            {
                header["error"] = LimitDiagnostic((string)error, ((string)error).Length / 2, ref truncated);
                bytes = Utf8.GetByteCount(header.ToString(Formatting.None));
            }
            foreach (var warning in result.Warnings ?? new List<string>())
            {
                string diagnostic = LimitDiagnostic(warning ?? "", 8192, ref truncated);
                int added = Utf8.GetByteCount(JsonConvert.SerializeObject(diagnostic)) + (warnings.Count == 0 ? 0 : 1);
                if (bytes + added + noticeBytes > HeaderLimit) { truncated = true; break; }
                warnings.Add(diagnostic);
                bytes += added;
            }
            if (truncated)
            {
                if (bytes + noticeBytes > HeaderLimit) throw new BridgeProtocolException(1009, "result metadata leaves no room for its diagnostic notice");
                warnings.Add(notice);
            }
            return header;
        }

        private static string LimitDiagnostic(string message, int characters, ref bool truncated)
        {
            if (message.Length <= characters) return message;
            truncated = true;
            if (characters > 0 && char.IsHighSurrogate(message[characters - 1])) characters--;
            return message.Substring(0, characters) + "…";
        }

        public static string HelloProblem(JObject header, int payloadBytes)
        {
            foreach (var field in new[] { "session", "project.id", "project.name", "app.version" })
                if (String(header, field) == null) return "hello is missing " + field;
            return payloadBytes != 0 ? "hello must not carry a payload" : null;
        }

        public static string PushProblem(JObject header, int payloadBytes)
        {
            if (String(header, "kind") == null) return "push is missing kind";
            if (String(header, "payload.format") == null) return "push is missing payload.format";
            var bytes = header.SelectToken("payload.bytes");
            if (bytes?.Type != JTokenType.Integer || !long.TryParse(bytes.ToString(), out var expected) || expected < 0)
                return "push is missing a nonnegative integer payload.bytes";
            return expected != payloadBytes ? $"the payload is {payloadBytes} bytes but payload.bytes says {expected}" : null;
        }

        public static byte[] Encode(JObject header, byte[] payload = null)
        {
            var json = Utf8.GetBytes(header.ToString(Formatting.None));
            if (json.Length > HeaderLimit) throw new BridgeProtocolException(1009, "header exceeds 64 KiB");
            payload = payload ?? Array.Empty<byte>();
            var bytes = new byte[4 + json.Length + payload.Length];
            var length = (uint)json.Length;
            for (var i = 0; i < 4; i++) bytes[i] = (byte)(length >> (8 * i));
            Buffer.BlockCopy(json, 0, bytes, 4, json.Length);
            Buffer.BlockCopy(payload, 0, bytes, 4 + json.Length, payload.Length);
            return bytes;
        }
    }

    /// <summary>Buffers only bytes actually received. Advertised lengths never allocate message bodies.</summary>
    public sealed class ForgeMessageBuffer : IDisposable
    {
        private readonly int payloadCap;
        private readonly MemoryStream bytes = new MemoryStream();
        private readonly byte[] prefix = new byte[4];
        private long announcedBytes;
        private int headerLength = -1;
        public long BufferedBytes => bytes.Length;

        public ForgeMessageBuffer(int payloadCap)
        {
            if (payloadCap < 0 || payloadCap > ForgeProtocol.MaximumPayload) throw new ArgumentOutOfRangeException(nameof(payloadCap));
            this.payloadCap = payloadCap;
        }

        public void AnnounceFrame(ulong length)
        {
            var limit = 4L + (headerLength < 0 ? ForgeProtocol.HeaderLimit : headerLength) + payloadCap;
            if (length > (ulong)(limit - announcedBytes)) throw new BridgeProtocolException(1009, "message exceeds the payload cap");
            announcedBytes += (long)length;
        }

        public void Append(byte[] source, int offset, int count)
        {
            if (bytes.Length + count > announcedBytes) throw new InvalidOperationException("Bytes exceed the announced frame length");
            if (bytes.Length < 4)
            {
                var take = Math.Min(4 - (int)bytes.Length, count);
                Buffer.BlockCopy(source, offset, prefix, (int)bytes.Length, take);
                bytes.Write(source, offset, take);
                offset += take;
                count -= take;
                if (bytes.Length == 4)
                {
                    var length = (uint)(prefix[0] | prefix[1] << 8 | prefix[2] << 16 | prefix[3] << 24);
                    if (length > ForgeProtocol.HeaderLimit) throw new BridgeProtocolException(1009, "header exceeds 64 KiB");
                    headerLength = (int)length;
                    if (announcedBytes > 4L + headerLength + payloadCap)
                        throw new BridgeProtocolException(1009, "message exceeds the payload cap");
                }
            }
            if (count > 0) bytes.Write(source, offset, count);
        }

        public ForgeMessage Complete()
        {
            if (bytes.Length != announcedBytes || headerLength < 0 || bytes.Length < 4L + headerLength)
                throw new BridgeProtocolException(1008, "message is shorter than its header length");
            var data = bytes.GetBuffer();
            JObject header;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(ForgeProtocol.Utf8.GetString(data, 4, headerLength))))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.MaxDepth = 64;
                    header = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new JsonReaderException("trailing JSON content");
                }
            }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException)
            {
                throw new BridgeProtocolException(1008, "header is not a valid UTF-8 JSON object");
            }
            var payload = new byte[(int)bytes.Length - 4 - headerLength];
            Buffer.BlockCopy(data, 4 + headerLength, payload, 0, payload.Length);
            return new ForgeMessage(header, payload);
        }

        public void Dispose() => bytes.Dispose();
    }
}
