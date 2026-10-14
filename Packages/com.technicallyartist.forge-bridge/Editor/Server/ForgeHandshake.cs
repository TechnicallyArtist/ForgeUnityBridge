using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeBridge.Editor
{
    public sealed class HandshakeDecision
    {
        public int Status;
        public string Reason;
        public string Origin;
        public string AcceptKey;
        public bool Accepted => Status == 101;
    }

    public static class ForgeHandshake
    {
        public const int MaximumHeadBytes = 16 * 1024;

        public static HandshakeDecision Evaluate(string request, int port)
        {
            if (request == null || request.Length > MaximumHeadBytes || !request.EndsWith("\r\n\r\n", StringComparison.Ordinal))
                return Refuse(400, "malformed request head or data before the upgrade");
            var lines = request.Substring(0, request.Length - 4).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var repeated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || !Regex.IsMatch(line.Substring(0, colon), @"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$") || line.Any(c => c == '\r' || c == '\n' || c == '\0'))
                    return Refuse(400, "malformed request header");
                var name = line.Substring(0, colon);
                if (headers.ContainsKey(name)) repeated.Add(name);
                else headers[name] = line.Substring(colon + 1).Trim();
            }
            if (!headers.TryGetValue("Host", out var host) || repeated.Contains("Host") ||
                !(host.Equals("127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase) || host.Equals("localhost:" + port, StringComparison.OrdinalIgnoreCase)))
                return Refuse(400, "Host must be 127.0.0.1:" + port + " or localhost:" + port);
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || requestLine[0] != "GET" || requestLine[1].Length == 0 || requestLine[2] != "HTTP/1.1" ||
                !HasToken(headers, "Upgrade", "websocket") || !HasToken(headers, "Connection", "upgrade") ||
                !headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13" || repeated.Contains("Sec-WebSocket-Version"))
                return Refuse(426, "not a WebSocket version 13 upgrade request");
            if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || repeated.Contains("Sec-WebSocket-Key") || !ValidKey(key))
                return Refuse(400, "Sec-WebSocket-Key is missing, repeated or malformed");
            if (!headers.TryGetValue("Origin", out var origin) || repeated.Contains("Origin") || !TryNormalizeOrigin(origin, out var normalized))
                return Refuse(403, "Origin is missing or is not a known Forge origin");
            return new HandshakeDecision { Status = 101, Origin = normalized, AcceptKey = ComputeAcceptKey(key) };
        }

        public static bool TryNormalizeOrigin(string text, out string normalized)
        {
            normalized = null;
            if (text == null || !Regex.IsMatch(text, @"^(https?)://([A-Za-z0-9.-]+)(?::([0-9]{1,5}))?$", RegexOptions.IgnoreCase)) return false;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Port < 1 || uri.Port > 65535) return false;
            var known = uri.Scheme == "https" && uri.Host == "forge.technicallyartist.com" && uri.Port == 443 ||
                uri.Scheme == "http" && (uri.Host == "localhost" || uri.Host == "127.0.0.1");
            if (!known) return false;
            normalized = uri.Scheme + "://" + uri.Host + ":" + uri.Port;
            return true;
        }

        public static string ComputeAcceptKey(string key)
        {
            using (var sha = SHA1.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        }

        public static byte[] Response(HandshakeDecision decision)
        {
            if (decision.Accepted)
                return Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + decision.AcceptKey + "\r\n\r\n");
            var phrase = decision.Status == 403 ? "Forbidden" : decision.Status == 426 ? "Upgrade Required" : "Bad Request";
            return Encoding.ASCII.GetBytes($"HTTP/1.1 {decision.Status} {phrase}\r\n" +
                (decision.Status == 426 ? "Upgrade: websocket\r\nSec-WebSocket-Version: 13\r\n" : "") + "Connection: close\r\nContent-Length: 0\r\n\r\n");
        }

        public static HandshakeDecision Refuse(int status, string reason) => new HandshakeDecision { Status = status, Reason = reason };

        private static bool ValidKey(string key)
        {
            if (key.Length != 24 || key.Any(char.IsWhiteSpace)) return false;
            try { return Convert.FromBase64String(key).Length == 16; }
            catch (FormatException) { return false; }
        }

        private static bool HasToken(Dictionary<string, string> headers, string name, string token) =>
            headers.TryGetValue(name, out var value) && value.Split(',').Any(part => part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));
    }
}
