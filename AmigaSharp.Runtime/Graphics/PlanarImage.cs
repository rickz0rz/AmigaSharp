using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AmigaSharp.Runtime.Graphics;

/// <summary>Converts a planar bitmap in memory to pens or to a PNG image, for tests and for debugging.</summary>
public static class PlanarImage
{
    /// <summary>The default colors of the Workbench screen (Kickstart 2): gray, black, white and blue.</summary>
    public static readonly uint[] WorkbenchColors = [0xAAAAAA, 0x000000, 0xFFFFFF, 0x6688BB];

    /// <summary>The pen of each pixel, as rows of the bitmap.</summary>
    public static int[,] ReadPens(Memory memory, uint bitMap)
    {
        var bytesPerRow = memory.Read16(bitMap + BitMapOffsets.BytesPerRow);
        var rows = memory.Read16(bitMap + BitMapOffsets.Rows);
        var depth = Math.Min((int)memory.Read8(bitMap + BitMapOffsets.Depth), BitMapOffsets.MaximumDepth);
        var pens = new int[rows, bytesPerRow * 8];
        for (var plane = 0; plane < depth; plane++)
        {
            var address = memory.Read32(bitMap + BitMapOffsets.Planes + (uint)plane * 4);
            if (address == 0)
                continue;
            var data = memory.Ram(address, bytesPerRow * rows);
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < bytesPerRow * 8; x++)
                {
                    if ((data[y * bytesPerRow + (x >> 3)] & (0x80 >> (x & 7))) != 0)
                        pens[y, x] |= 1 << plane;
                }
            }
        }

        return pens;
    }

    /// <summary>Shows the pens as text: one character for each pixel, "." for pen 0.</summary>
    public static string ToText(int[,] pens, int x, int y, int width, int height)
    {
        var text = new StringBuilder();
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
                text.Append(pens[row, column] == 0 ? '.' : (char)('0' + pens[row, column] % 10));
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Encodes the bitmap as a PNG image. The palette has a 0xRRGGBB color for each pen. Each pixel becomes a square
    /// of <paramref name="scale"/> by <paramref name="scale"/> pixels.
    /// </summary>
    public static byte[] ToPng(Memory memory, uint bitMap, IReadOnlyList<uint> palette, int scale = 1)
    {
        var pens = ReadPens(memory, bitMap);
        var height = pens.GetLength(0) * scale;
        var width = pens.GetLength(1) * scale;

        // Each row starts with filter type 0 (none), then 3 bytes for each pixel.
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            for (var x = 0; x < width; x++)
            {
                var color = palette[pens[y / scale, x / scale] % palette.Count];
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
