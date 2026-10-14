using System;
using System.IO;

namespace ForgeBridge.Editor
{
    public sealed class WebSocketFrameHeader
    {
        public bool Final;
        public byte Opcode;
        public ulong Length;
        public byte[] Mask;
        public bool Control => Opcode >= 8;
    }

    public static class WebSocketCodec
    {
        public static int HeaderBytes(byte lengthByte) => 6 + ((lengthByte & 127) == 126 ? 2 : (lengthByte & 127) == 127 ? 8 : 0);

        public static WebSocketFrameHeader DecodeHeader(byte[] data)
        {
            if (data.Length < 2 || data.Length < HeaderBytes(data[1])) throw new ArgumentException("Incomplete frame header");
            ValidatePrefix(data[0], data[1]);
            var final = (data[0] & 128) != 0;
            var opcode = (byte)(data[0] & 15);
            var size = data[1] & 127;
            ulong length = (ulong)size;
            var maskOffset = 2;
            if (size == 126 || size == 127)
            {
                var count = size == 126 ? 2 : 8;
                length = 0;
                for (var i = 0; i < count; i++) length = (length << 8) | data[2 + i];
                if ((size == 126 && length < 126) || (size == 127 && (length < 65536 || (length & (1UL << 63)) != 0)))
                    throw new BridgeProtocolException(1002, "invalid frame length encoding");
                maskOffset += count;
            }
            var mask = new byte[4];
            Buffer.BlockCopy(data, maskOffset, mask, 0, 4);
            return new WebSocketFrameHeader { Final = final, Opcode = opcode, Length = length, Mask = mask };
        }

        public static void ValidatePrefix(byte first, byte second)
        {
            var opcode = first & 15;
            if ((first & 112) != 0) throw new BridgeProtocolException(1002, "reserved frame bits set");
            if ((second & 128) == 0) throw new BridgeProtocolException(1002, "client frame is not masked");
            if (opcode != 0 && opcode != 1 && opcode != 2 && opcode != 8 && opcode != 9 && opcode != 10)
                throw new BridgeProtocolException(1002, "unknown frame opcode");
            if (opcode >= 8 && ((first & 128) == 0 || (second & 127) > 125))
                throw new BridgeProtocolException(1002, "fragmented or oversized control frame");
        }

        public static void Unmask(byte[] bytes, int count, byte[] mask, ulong payloadOffset)
        {
            for (var i = 0; i < count; i++) bytes[i] ^= mask[(int)((payloadOffset + (ulong)i) & 3)];
        }

        public static byte[] Encode(byte opcode, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            using (var result = new MemoryStream())
            {
                result.WriteByte((byte)(128 | opcode));
                if (payload.Length < 126) result.WriteByte((byte)payload.Length);
                else if (payload.Length < 65536)
                {
                    result.WriteByte(126);
                    result.WriteByte((byte)(payload.Length >> 8));
                    result.WriteByte((byte)payload.Length);
                }
                else
                {
                    result.WriteByte(127);
                    for (var shift = 56; shift >= 0; shift -= 8) result.WriteByte((byte)((ulong)payload.Length >> shift));
                }
                result.Write(payload, 0, payload.Length);
                return result.ToArray();
            }
        }

        public static byte[] Close(ushort code, string reason)
        {
            var text = ForgeProtocol.Utf8.GetBytes(reason ?? "");
            var count = Math.Min(123, text.Length);
            while (count > 0 && count < text.Length && (text[count] & 192) == 128) count--;
            var payload = new byte[2 + count];
            payload[0] = (byte)(code >> 8);
            payload[1] = (byte)code;
            Buffer.BlockCopy(text, 0, payload, 2, count);
            return Encode(8, payload);
        }

        public static void ValidateClose(byte[] payload)
        {
            if (payload.Length == 1) throw new BridgeProtocolException(1002, "malformed close payload");
            if (payload.Length < 2) return;
            var code = payload[0] << 8 | payload[1];
            if (code < 1000 || code >= 5000 || code == 1004 || code == 1005 || code == 1006 || code == 1015 || code >= 1016 && code < 3000)
                throw new BridgeProtocolException(1002, "invalid close status");
            try { ForgeProtocol.Utf8.GetString(payload, 2, payload.Length - 2); }
            catch (System.Text.DecoderFallbackException) { throw new BridgeProtocolException(1007, "close reason is not UTF-8"); }
        }
    }
}
