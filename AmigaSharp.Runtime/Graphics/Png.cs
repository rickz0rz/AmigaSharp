using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AmigaSharp.Runtime.Graphics;

/// <summary>Encodes pixels as a PNG image, for screenshots and for tests.</summary>
public static class Png
{
    /// <summary>Encodes 0xAARRGGBB pixels, row by row. The alpha channel is not used.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<uint> pixels)
    {
        // Each row starts with filter type 0 (none), then 3 bytes for each pixel.
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            for (var x = 0; x < width; x++)
            {
                var color = pixels[y * width + x];
                raw[row + 1 + x * 3] = (byte)(color >> 16);
                raw[row + 2 + x * 3] = (byte)(color >> 8);
                raw[row + 3 + x * 3] = (byte)color;
            }
        }

        using var image = new MemoryStream();
        image.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits for each channel
        header[9] = 2; // RGB
        WriteChunk(image, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            WriteChunk(image, "IDAT", compressed.ToArray());
        }

        WriteChunk(image, "IEND", []);
        return image.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typeAndData);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
        }

        return ~crc;
    }
}
