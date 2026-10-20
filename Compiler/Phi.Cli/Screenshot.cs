using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Phi.Cli
{
    /// <summary>
    /// The screen of a test machine, from QEMU's screendump (a PPM file): reading pixels for
    /// `pixel` checks, and saving it as a PNG for `screenshot`.
    /// </summary>
    public sealed class Screenshot
    {
        public int Width { get; }
        public int Height { get; }
        readonly byte[] rgb;

        Screenshot(int width, int height, byte[] rgb)
        {
            Width = width;
            Height = height;
            this.rgb = rgb;
        }

        /// <summary>0xRRGGBB at (x, y).</summary>
        public int Pixel(int x, int y)
        {
            int i = (y * Width + x) * 3;
            return (rgb[i] << 16) | (rgb[i + 1] << 8) | rgb[i + 2];
        }

        /// <summary>Reads a binary PPM (P6, 8 bits per channel), or returns null if it isn't one.</summary>
        public static Screenshot? ReadPpm(byte[] data)
        {
            int at = 0;
            string? Token()
            {
                while (at < data.Length)
                {
                    if (data[at] == '#') { while (at < data.Length && data[at] != '\n') at++; }
                    else if (char.IsWhiteSpace((char)data[at])) at++;
                    else break;
                }
                int start = at;
                while (at < data.Length && !char.IsWhiteSpace((char)data[at])) at++;
                return at > start ? Encoding.ASCII.GetString(data, start, at - start) : null;
            }

            if (Token() != "P6") return null;
            if (!int.TryParse(Token(), out int w) || !int.TryParse(Token(), out int h) || Token() != "255") return null;
            at++; // the single whitespace after the header

            int size = w * h * 3;
            if (w <= 0 || h <= 0 || data.Length - at < size) return null;
            return new Screenshot(w, h, data.AsSpan(at, size).ToArray());
        }

        public void SavePng(string path)
        {
            // each row: filter type 0 (none), then the RGB bytes
            var raw = new MemoryStream();
            for (int y = 0; y < Height; y++)
            {
                raw.WriteByte(0);
                raw.Write(rgb, y * Width * 3, Width * 3);
            }

            var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                raw.WriteTo(z);

            using var file = File.Create(path);
            file.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), Width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
            header[8] = 8;  // bits per channel
            header[9] = 2;  // RGB
            WriteChunk(file, "IHDR", header);
            WriteChunk(file, "IDAT", compressed.ToArray());
            WriteChunk(file, "IEND", Array.Empty<byte>());
        }

        static void WriteChunk(Stream s, string type, byte[] body)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
            s.Write(length);

            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(body);

            uint crc = Crc32(Crc32(0xFFFFFFFF, typeBytes), body) ^ 0xFFFFFFFF;
            var crcBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            s.Write(crcBytes);
        }

        static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        static uint Crc32(uint crc, byte[] bytes)
        {
            foreach (byte b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
