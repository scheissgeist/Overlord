using System;
using System.Collections.Generic;
using System.Text;

namespace Overlord
{
    public static class BinaryFrameProtocol
    {
        private static readonly byte[] Magic = { (byte)'O', (byte)'V', (byte)'L', (byte)'1' };

        public static byte[] EncodeMapFrame(Dictionary<string, object> metadata, byte[] imageBytes)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            if (imageBytes == null) throw new ArgumentNullException(nameof(imageBytes));

            var cleanMetadata = new Dictionary<string, object>(metadata)
            {
                ["type"] = StateProtocol.MapFrame,
                ["binary"] = true,
                ["encoding"] = "jpeg",
                ["dataBytes"] = imageBytes.Length
            };
            cleanMetadata.Remove("data");

            byte[] metadataBytes = Encoding.UTF8.GetBytes(JsonHelper.ToJson(cleanMetadata));
            byte[] packet = new byte[8 + metadataBytes.Length + imageBytes.Length];

            Buffer.BlockCopy(Magic, 0, packet, 0, Magic.Length);
            WriteUInt32LittleEndian(packet, 4, (uint)metadataBytes.Length);
            Buffer.BlockCopy(metadataBytes, 0, packet, 8, metadataBytes.Length);
            Buffer.BlockCopy(imageBytes, 0, packet, 8 + metadataBytes.Length, imageBytes.Length);

            return packet;
        }

        private static void WriteUInt32LittleEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xff);
            buffer[offset + 1] = (byte)((value >> 8) & 0xff);
            buffer[offset + 2] = (byte)((value >> 16) & 0xff);
            buffer[offset + 3] = (byte)((value >> 24) & 0xff);
        }
    }
}
