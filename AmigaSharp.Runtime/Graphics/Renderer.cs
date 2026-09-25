namespace AmigaSharp.Runtime.Graphics;

/// <summary>
/// Draws into the planar bitmap of a RastPort in memory. Pixel (x, y) of plane p is bit 7 - (x % 8) of byte
/// y * BytesPerRow + x / 8 of the plane. The RastPort has no layer, so the renderer clips to the bitmap.
/// </summary>
public sealed class Renderer(Memory memory)
{
    /// <summary>The state of a RastPort that the drawing functions use.</summary>
    public readonly struct Target
    {
        public required uint[] Planes { get; init; }
        public required int BytesPerRow { get; init; }
        public required int Rows { get; init; }
        public required byte Mask { get; init; }
        public required byte FgPen { get; init; }
        public required byte BgPen { get; init; }
        public required DrawMode Mode { get; init; }

        public int Width => BytesPerRow * 8;
        public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Rows;
    }

    /// <summary>Reads the bitmap and the pens of a RastPort. Returns null if the RastPort has no bitmap.</summary>
    public Target? TargetOf(uint rastPort)
    {
        var bitMap = memory.Read32(rastPort + RastPortOffsets.BitMap);
        if (bitMap == 0)
            return null;
        return TargetOf(bitMap, memory.Read8(rastPort + RastPortOffsets.Mask), memory.Read8(rastPort + RastPortOffsets.FgPen),
            memory.Read8(rastPort + RastPortOffsets.BgPen), (DrawMode)memory.Read8(rastPort + RastPortOffsets.DrawMode));
    }

    public Target TargetOf(uint bitMap, byte mask, byte fgPen, byte bgPen, DrawMode mode)
    {
        var depth = Math.Min((int)memory.Read8(bitMap + BitMapOffsets.Depth), BitMapOffsets.MaximumDepth);
        var planes = new uint[depth];
        for (var i = 0; i < depth; i++)
            planes[i] = memory.Read32(bitMap + BitMapOffsets.Planes + (uint)i * 4);
        return new Target
        {
            Planes = planes,
            BytesPerRow = memory.Read16(bitMap + BitMapOffsets.BytesPerRow),
            Rows = memory.Read16(bitMap + BitMapOffsets.Rows),
            Mask = mask,
            FgPen = fgPen,
            BgPen = bgPen,
            Mode = mode,
        };
    }

    public int ReadPixel(in Target target, int x, int y)
    {
        if (!target.Contains(x, y))
            return -1;
        var pen = 0;
        for (var plane = 0; plane < target.Planes.Length; plane++)
        {
            if (target.Planes[plane] != 0 && GetBit(target, plane, x, y))
                pen |= 1 << plane;
        }

        return pen;
    }

    /// <summary>Writes a pen to a pixel. Only the planes in the write mask change.</summary>
    public void WritePixel(in Target target, int x, int y, int pen)
    {
        if (!target.Contains(x, y))
            return;
        for (var plane = 0; plane < target.Planes.Length; plane++)
        {
            if ((target.Mask & (1 << plane)) != 0 && target.Planes[plane] != 0)
                SetBit(target, plane, x, y, (pen & (1 << plane)) != 0);
        }
    }

    /// <summary>Inverts a pixel in the planes of the write mask.</summary>
    public void ComplementPixel(in Target target, int x, int y)
    {
        if (!target.Contains(x, y))
            return;
        for (var plane = 0; plane < target.Planes.Length; plane++)
        {
            if ((target.Mask & (1 << plane)) != 0 && target.Planes[plane] != 0)
                SetBit(target, plane, x, y, !GetBit(target, plane, x, y));
        }
    }

    /// <summary>
    /// Draws a pixel of a pattern or a glyph in the draw mode of the target. <paramref name="set"/> is the bit of
    /// the pattern or the glyph at the pixel.
    /// </summary>
    public void PatternPixel(in Target target, int x, int y, bool set)
    {
        if (target.Mode.HasFlag(DrawMode.InverseVideo))
            set = !set;
        if (target.Mode.HasFlag(DrawMode.Complement))
        {
            if (set)
                ComplementPixel(target, x, y);
        }
        else if (set)
        {
            WritePixel(target, x, y, target.FgPen);
        }
        else if (target.Mode.HasFlag(DrawMode.Jam2))
        {
            WritePixel(target, x, y, target.BgPen);
        }
    }

    /// <summary>RectFill: fills the rectangle, both corners included, with the foreground pen.</summary>
    public void FillRectangle(in Target target, int xMin, int yMin, int xMax, int yMax)
    {
        xMin = Math.Max(xMin, 0);
        yMin = Math.Max(yMin, 0);
        xMax = Math.Min(xMax, target.Width - 1);
        yMax = Math.Min(yMax, target.Rows - 1);
        for (var y = yMin; y <= yMax; y++)
        {
            for (var x = xMin; x <= xMax; x++)
                PatternPixel(target, x, y, set: true);
        }
    }

    /// <summary>SetRast: sets all pixels of the bitmap to the pen.</summary>
    public void Clear(in Target target, int pen)
    {
        var size = target.BytesPerRow * target.Rows;
        for (var plane = 0; plane < target.Planes.Length; plane++)
        {
            if (target.Planes[plane] == 0 || (target.Mask & (1 << plane)) == 0)
                continue;
            memory.Ram(target.Planes[plane], size).Fill((pen & (1 << plane)) != 0 ? (byte)0xFF : (byte)0);
        }
    }

    /// <summary>
    /// Draw: draws a line from (x0, y0) to (x1, y1), both ends included. Each pixel uses the next bit of the line
    /// pattern, from bit 15 down. Returns the number of pixels.
    /// </summary>
    public int DrawLine(in Target target, int x0, int y0, int x1, int y1, ushort pattern, int patternStart)
    {
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var stepX = x0 < x1 ? 1 : -1;
        var stepY = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        var count = 0;
        while (true)
        {
            var bit = (pattern >> (15 - (patternStart + count) % 16)) & 1;
            PatternPixel(target, x0, y0, bit != 0);
            count++;
            if (x0 == x1 && y0 == y1)
                return count;
            var doubled = 2 * error;
            if (doubled >= dy)
            {
                error += dy;
                x0 += stepX;
            }

            if (doubled <= dx)
            {
                error += dx;
                y0 += stepY;
            }
        }
    }

    /// <summary>
    /// Copies a rectangle between bitmaps with a blitter minterm. The source is B and the destination is C, as for
    /// BltBitMap: the result bit is bit (4 + 2 * B + C) of the minterm.
    /// </summary>
    public void Blit(in Target source, int sourceX, int sourceY, in Target destination, int destinationX, int destinationY,
        int width, int height, byte minterm)
    {
        var planes = Math.Min(source.Planes.Length, destination.Planes.Length);
        for (var plane = 0; plane < planes; plane++)
        {
            if ((destination.Mask & (1 << plane)) == 0 || destination.Planes[plane] == 0)
                continue;

            // Copy the source first, so that an overlapping blit in the same bitmap reads the old pixels.
            var bits = new bool[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    bits[y * width + x] = source.Contains(sourceX + x, sourceY + y) && source.Planes[plane] != 0
                                          && GetBit(source, plane, sourceX + x, sourceY + y);
                }
            }

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var targetX = destinationX + x;
                    var targetY = destinationY + y;
                    if (!destination.Contains(targetX, targetY))
                        continue;
                    var b = bits[y * width + x] ? 1 : 0;
                    var c = GetBit(destination, plane, targetX, targetY) ? 1 : 0;
                    SetBit(destination, plane, targetX, targetY, ((minterm >> (4 + 2 * b + c)) & 1) != 0);
                }
            }
        }
    }

    private bool GetBit(in Target target, int plane, int x, int y)
    {
        var address = target.Planes[plane] + (uint)(y * target.BytesPerRow + (x >> 3));
        return (memory.Read8(address) & (0x80 >> (x & 7))) != 0;
    }

    private void SetBit(in Target target, int plane, int x, int y, bool value)
    {
        var address = target.Planes[plane] + (uint)(y * target.BytesPerRow + (x >> 3));
        var bit = (byte)(0x80 >> (x & 7));
        var old = memory.Read8(address);
        memory.Write8(address, value ? (byte)(old | bit) : (byte)(old & ~bit));
    }
}
