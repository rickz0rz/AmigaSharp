using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Graphics;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// graphics.library. The drawing functions draw into planar bitmaps in memory. The RastPorts have no layers, so the
/// drawing clips to the bitmap. The display (views, viewports and copper lists) comes from the hardware model.
/// </summary>
public class GraphicsLibrary(Core core) : AbstractLibrary
{
    private readonly Memory _memory = core.Memory;
    private readonly Renderer _renderer = new(core.Memory);
    private readonly Fonts _fonts = new(core);

    public override string Name => "graphics.library";
    public override ushort Version => 40;
    public override ushort Revision => 42;
    public override ushort PositiveSize => (ushort)GfxBaseOffsets.Size;
    public override short LowestOffset => -1062;

    /// <summary>The font list of GfxBase. diskfont.library adds its fonts here.</summary>
    public uint FontList => Base + GfxBaseOffsets.TextFonts;

    public Fonts Fonts => _fonts;

    public override void Initialize()
    {
        ExecList.Initialize(_memory, FontList, TextFontOffsets.NodeTypeFont);
        var topaz = _fonts.CreateBuiltInFont();
        ExecList.AddTail(_memory, FontList, topaz);
        _memory.Write32(Base + GfxBaseOffsets.DefaultFont, topaz);

        var pal = core.Chipset.Beam.Standard == VideoStandard.Pal;
        _memory.Write16(Base + GfxBaseOffsets.DisplayFlags, pal ? GfxBaseOffsets.Pal : GfxBaseOffsets.Ntsc);
        _memory.Write8(Base + GfxBaseOffsets.VBlank, pal ? (byte)50 : (byte)60);
        _memory.Write16(Base + GfxBaseOffsets.MaxDisplayRow, (ushort)(core.Chipset.Beam.LinesPerFrame - 1));
        _memory.Write16(Base + GfxBaseOffsets.MaxDisplayColumn, 454);
        _memory.Write16(Base + GfxBaseOffsets.NormalDisplayRows, pal ? (ushort)256 : (ushort)200);
        _memory.Write16(Base + GfxBaseOffsets.NormalDisplayColumns, 640);
        _memory.Write16(Base + GfxBaseOffsets.NormalDpmX, 1280);
        _memory.Write16(Base + GfxBaseOffsets.NormalDpmY, 1280);
        _memory.Write16(Base + GfxBaseOffsets.MicrosPerLine, pal ? (ushort)64 : (ushort)63);
        _memory.Write8(Base + GfxBaseOffsets.ChipRevBits0, core.Chipset.Aga ? GfxBaseOffsets.AgaChips : GfxBaseOffsets.EcsChips);
    }

    // planes = BltBitMap(srcBitMap, xSrc, ySrc, destBitMap, xDest, yDest, xSize, ySize, minterm, mask, tempA)
    // D0                 A0         D0    D1    A1          D2     D3     D4     D5     D6       D7    A2
    [LibraryFunctionOffset(-30)]
    public int BltBitMap([A0] uint source, [D0] short sourceX, [D1] short sourceY, [A1] uint destination,
        [D2] short destinationX, [D3] short destinationY, [D4] short width, [D5] short height, [D6] byte minterm,
        [D7] byte mask, [A2] uint temporary)
    {
        var from = _renderer.TargetOf(source, 0xFF, 0, 0, DrawMode.Jam2);
        var to = _renderer.TargetOf(destination, mask, 0, 0, DrawMode.Jam2);
        _renderer.Blit(from, sourceX, sourceY, to, destinationX, destinationY, width, height, minterm);
        return Math.Min(from.Planes.Length, to.Planes.Length);
    }

    // size = TextLength(rp, string, count)
    // D0                A1  A0      D0
    [LibraryFunctionOffset(-54)]
    public int TextLength([A1] uint rastPort, [A0] uint text, [D0] ushort count)
    {
        var font = _memory.Read32(rastPort + RastPortOffsets.Font);
        var spacing = (short)_memory.Read16(rastPort + RastPortOffsets.TxSpacing);
        var width = 0;
        for (uint i = 0; i < count; i++)
        {
            var glyph = Glyph(font, _memory.Read8(text + i));
            width += glyph.Kern + glyph.Space + spacing;
        }

        return width;
    }

    // Text(rp, string, count)
    //      A1  A0      D0
    // The text starts at the current position. The current position is on the baseline.
    [LibraryFunctionOffset(-60)]
    public void Text([A1] uint rastPort, [A0] uint text, [D0] ushort count)
    {
        var font = _memory.Read32(rastPort + RastPortOffsets.Font);
        if (font == 0 || _renderer.TargetOf(rastPort) is not { } target)
            return;

        int x = (short)_memory.Read16(rastPort + RastPortOffsets.X);
        var top = (short)_memory.Read16(rastPort + RastPortOffsets.Y) - _memory.Read16(font + TextFontOffsets.Baseline);
        var height = _memory.Read16(font + TextFontOffsets.YSize);
        var modulo = _memory.Read16(font + TextFontOffsets.Modulo);
        var spacing = (short)_memory.Read16(rastPort + RastPortOffsets.TxSpacing);
        var drawCell = target.Mode.HasFlag(DrawMode.Jam2) || target.Mode.HasFlag(DrawMode.InverseVideo);
        var color = ColorFont.Read(_memory, font);

        for (uint i = 0; i < count; i++)
        {
            var glyph = Glyph(font, _memory.Read8(text + i));
            var glyphX = x + glyph.Kern;
            // With JAM2, the background pen fills the cell of the character: its space, from the current position.
            var cellStart = drawCell ? Math.Min(x, glyphX) : glyphX;
            var cellEnd = drawCell ? Math.Max(x + glyph.Space, glyphX + glyph.Width) : glyphX + glyph.Width;
            for (var row = 0; row < height; row++)
            {
                var rowOffset = (uint)(row * modulo);
                for (var column = cellStart; column < cellEnd; column++)
                {
                    var bitIndex = column - glyphX;
                    var inGlyph = bitIndex >= 0 && bitIndex < glyph.Width;
                    if (color is { } colorFont)
                    {
                        var value = inGlyph ? colorFont.ColorAt(_memory, rowOffset, glyph.Offset + bitIndex) : 0;
                        if (value != 0 && !target.Mode.HasFlag(DrawMode.Complement))
                            _renderer.WritePixel(target, column, top + row, colorFont.Pen(value, target.FgPen));
                        else if (value != 0 || drawCell)
                            _renderer.PatternPixel(target, column, top + row, value != 0);
                        continue;
                    }

                    var set = inGlyph && GlyphBit(_memory.Read32(font + TextFontOffsets.CharData) + rowOffset, glyph.Offset + bitIndex);
                    if (set || drawCell)
                        _renderer.PatternPixel(target, column, top + row, set);
                }
            }

            x += glyph.Space + spacing;
        }

        _memory.Write16(rastPort + RastPortOffsets.X, (ushort)x);
    }

    private bool GlyphBit(uint rowAddress, int bit) =>
        (_memory.Read8(rowAddress + (uint)(bit >> 3)) & (0x80 >> (bit & 7))) != 0;

    /// <summary>
    /// The planes of a ColorTextFont. A pixel of a glyph has a color: bit k of the color is the bit in font plane k.
    /// Font plane k goes to the destination plane of bit k of PlanePick, counted from bit 0. Each other destination
    /// plane gets its bit of PlaneOnOff. The pixels of color 0 are the background.
    /// </summary>
    private sealed class ColorFont
    {
        private readonly uint[] _planes;
        private readonly byte _planePick;
        private readonly byte _planeOnOff;
        private readonly int? _mappedColor;

        private ColorFont(uint[] planes, byte planePick, byte planeOnOff, int? mappedColor)
        {
            _planes = planes;
            _planePick = planePick;
            _planeOnOff = planeOnOff;
            _mappedColor = mappedColor;
        }

        /// <summary>Returns null if the font is not a color font.</summary>
        public static ColorFont? Read(Memory memory, uint font)
        {
            if ((memory.Read8(font + TextFontOffsets.Style) & TextFontOffsets.ColorFontStyle) == 0)
                return null;
            var depth = Math.Min((int)memory.Read8(font + ColorTextFontOffsets.Depth), ColorTextFontOffsets.MaximumDepth);
            var planes = new uint[depth];
            for (var k = 0; k < depth; k++)
                planes[k] = memory.Read32(font + ColorTextFontOffsets.CharData + (uint)k * 4);

            // With MAPCOLOR, the color FgColor gets the foreground pen. The Prevue fonts have MAPCOLOR and an FgColor
            // of $FF, which is not between Low and High. On the real machine, color 1 of these fonts gets the
            // foreground pen: the text is in the pen, and the outline (color 2) stays. So color 1 is the default.
            int? mapped = null;
            if ((memory.Read16(font + ColorTextFontOffsets.Flags) & ColorTextFontOffsets.MapColor) != 0)
            {
                var fgColor = memory.Read8(font + ColorTextFontOffsets.FgColor);
                var inRange = fgColor >= memory.Read8(font + ColorTextFontOffsets.Low)
                              && fgColor <= memory.Read8(font + ColorTextFontOffsets.High);
                mapped = inRange ? fgColor : 1;
            }

            return new ColorFont(planes, memory.Read8(font + ColorTextFontOffsets.PlanePick),
                memory.Read8(font + ColorTextFontOffsets.PlaneOnOff), mapped);
        }

        public int ColorAt(Memory memory, uint rowOffset, int bit)
        {
            var value = 0;
            for (var k = 0; k < _planes.Length; k++)
            {
                if (_planes[k] != 0 && (memory.Read8(_planes[k] + rowOffset + (uint)(bit >> 3)) & (0x80 >> (bit & 7))) != 0)
                    value |= 1 << k;
            }

            return value;
        }

        /// <summary>The pen of a pixel of the color in the destination planes.</summary>
        public int Pen(int value, int fgPen)
        {
            if (value == _mappedColor)
                return fgPen;
            var pen = 0;
            var fontPlane = 0;
            for (var plane = 0; plane < 8; plane++)
            {
                var bit = (_planeOnOff >> plane) & 1;
                if ((_planePick & (1 << plane)) != 0)
                    bit = fontPlane < _planes.Length ? (value >> fontPlane++) & 1 : bit;
                pen |= bit << plane;
            }

            return pen;
        }
    }

    // SetFont(rp, font)
    //         A1  A0
    [LibraryFunctionOffset(-66)]
    public void SetFont([A1] uint rastPort, [A0] uint font)
    {
        if (font == 0)
            return;
        _memory.Write32(rastPort + RastPortOffsets.Font, font);
        _memory.Write16(rastPort + RastPortOffsets.TxHeight, _memory.Read16(font + TextFontOffsets.YSize));
        _memory.Write16(rastPort + RastPortOffsets.TxWidth, _memory.Read16(font + TextFontOffsets.XSize));
        _memory.Write16(rastPort + RastPortOffsets.TxBaseline, _memory.Read16(font + TextFontOffsets.Baseline));
    }

    // font = OpenFont(textAttr)
    // D0              A0
    [LibraryFunctionOffset(-72)]
    public uint OpenFont([A0] uint textAttr)
    {
        var name = _memory.ReadCString(_memory.Read32(textAttr + TextAttrOffsets.Name));
        var font = _fonts.Find(FontList, name, _memory.Read16(textAttr + TextAttrOffsets.YSize), exactSize: false);
        if (font != 0)
            ChangeAccessors(font, 1);
        return font;
    }

    // CloseFont(font)
    //           A1
    [LibraryFunctionOffset(-78)]
    public void CloseFont([A1] uint font)
    {
        if (font != 0)
            ChangeAccessors(font, -1);
    }

    // style = AskSoftStyle(rp)
    // D0                   A1
    // The runtime does not draw the algorithmic styles, so no style is possible.
    [LibraryFunctionOffset(-84)]
    public uint AskSoftStyle([A1] uint rastPort) => 0;

    // newStyle = SetSoftStyle(rp, style, enable)
    // D0                      A1  D0     D1
    [LibraryFunctionOffset(-90)]
    public uint SetSoftStyle([A1] uint rastPort, [D0] uint style, [D1] uint enable)
    {
        var old = _memory.Read8(rastPort + RastPortOffsets.AlgoStyle);
        var value = (byte)((old & ~enable) | (style & enable));
        _memory.Write8(rastPort + RastPortOffsets.AlgoStyle, value);
        return value;
    }

    // InitRastPort(rp)
    //              A1
    [LibraryFunctionOffset(-198)]
    public void InitRastPort([A1] uint rastPort)
    {
        _memory.Ram(rastPort, (int)RastPortOffsets.Size).Clear();
        _memory.Write8(rastPort + RastPortOffsets.Mask, 0xFF);
        _memory.Write8(rastPort + RastPortOffsets.FgPen, 0xFF);
        _memory.Write8(rastPort + RastPortOffsets.AOlPen, 0xFF);
        _memory.Write8(rastPort + RastPortOffsets.DrawMode, (byte)DrawMode.Jam2);
        _memory.Write16(rastPort + RastPortOffsets.LinePattern, 0xFFFF);
        _memory.Write16(rastPort + RastPortOffsets.PenWidth, 1);
        _memory.Write16(rastPort + RastPortOffsets.PenHeight, 1);
        SetFont(rastPort, _memory.Read32(Base + GfxBaseOffsets.DefaultFont));
    }

    // LoadView(view)
    //          A1
    // The runtime has no system copper list. With a null View, the display shows the copper list of the program, or
    // nothing. With a View, the copper starts at the long-frame list of the View from the next frame.
    [LibraryFunctionOffset(-222)]
    public void LoadView([A1] uint view)
    {
        _memory.Write32(Base + GfxBaseOffsets.ActiView, view);
        if (view == 0)
            return;
        var copperList = _memory.Read32(view + ViewOffsets.LofCprList);
        if (copperList == 0)
            return;
        var start = _memory.Read32(copperList + CprListOffsets.Start);
        _memory.Write32(Base + GfxBaseOffsets.LofList, start);
        _memory.Write16(Hardware.CustomRegister.Base + Hardware.CustomRegister.Cop1lc, (ushort)(start >> 16));
        _memory.Write16(Hardware.CustomRegister.Base + Hardware.CustomRegister.Cop1lc + 2, (ushort)start);
    }

    // WaitBlit()
    // The runtime blits at once, so the blitter is never busy.
    [LibraryFunctionOffset(-228)]
    public void WaitBlit()
    {
    }

    // SetRast(rp, pen)
    //         A1  D0
    [LibraryFunctionOffset(-234)]
    public void SetRast([A1] uint rastPort, [D0] byte pen)
    {
        if (_renderer.TargetOf(rastPort) is { } target)
            _renderer.Clear(target, pen);
    }

    // Move(rp, x, y)
    //      A1  D0 D1
    [LibraryFunctionOffset(-240)]
    public void Move([A1] uint rastPort, [D0] short x, [D1] short y)
    {
        _memory.Write16(rastPort + RastPortOffsets.X, (ushort)x);
        _memory.Write16(rastPort + RastPortOffsets.Y, (ushort)y);
    }

    // Draw(rp, x, y)
    //      A1  D0 D1
    [LibraryFunctionOffset(-246)]
    public void Draw([A1] uint rastPort, [D0] short x, [D1] short y)
    {
        if (_renderer.TargetOf(rastPort) is { } target)
        {
            var count = _memory.Read8(rastPort + RastPortOffsets.LinePatternCount);
            var drawn = _renderer.DrawLine(target, (short)_memory.Read16(rastPort + RastPortOffsets.X),
                (short)_memory.Read16(rastPort + RastPortOffsets.Y), x, y,
                _memory.Read16(rastPort + RastPortOffsets.LinePattern), count);
            _memory.Write8(rastPort + RastPortOffsets.LinePatternCount, (byte)((count + drawn) % 16));
        }

        Move(rastPort, x, y);
    }

    // WaitTOF()
    // Waits for the start of the next frame.
    [LibraryFunctionOffset(-270)]
    public void WaitTOF() => core.WaitForNextFrame();

    // BltClear(memBlock, bytecount, flags)
    //          A1        D0         D1
    [LibraryFunctionOffset(-300)]
    public void BltClear([A1] uint block, [D0] uint count, [D1] uint flags)
    {
        // Bit 1 of the flags: the count is rows in the high word and bytes per row in the low word.
        if ((flags & 2) != 0)
            count = (count >> 16) * (count & 0xFFFF);
        _memory.Ram(block, (int)count).Clear();
    }

    // RectFill(rp, xMin, yMin, xMax, yMax)
    //          A1  D0    D1    D2    D3
    [LibraryFunctionOffset(-306)]
    public void RectFill([A1] uint rastPort, [D0] short xMin, [D1] short yMin, [D2] short xMax, [D3] short yMax)
    {
        if (_renderer.TargetOf(rastPort) is { } target)
            _renderer.FillRectangle(target, xMin, yMin, xMax, yMax);
    }

    // penno = ReadPixel(rp, x, y)
    // D0                A1  D0 D1
    [LibraryFunctionOffset(-318)]
    public int ReadPixel([A1] uint rastPort, [D0] short x, [D1] short y) =>
        _renderer.TargetOf(rastPort) is { } target ? _renderer.ReadPixel(target, x, y) : -1;

    // error = WritePixel(rp, x, y)
    // D0                 A1  D0 D1
    [LibraryFunctionOffset(-324)]
    public int WritePixel([A1] uint rastPort, [D0] short x, [D1] short y)
    {
        if (_renderer.TargetOf(rastPort) is not { } target || !target.Contains(x, y))
            return -1;
        _renderer.PatternPixel(target, x, y, set: true);
        return 0;
    }

    // PolyDraw(rp, count, polyTable)
    //          A1  D0     A0
    [LibraryFunctionOffset(-336)]
    public void PolyDraw([A1] uint rastPort, [D0] short count, [A0] uint table)
    {
        for (var i = 0; i < count; i++)
            Draw(rastPort, (short)_memory.Read16(table + (uint)i * 4), (short)_memory.Read16(table + (uint)i * 4 + 2));
    }

    // SetAPen(rp, pen)
    //         A1  D0
    [LibraryFunctionOffset(-342)]
    public void SetAPen([A1] uint rastPort, [D0] byte pen) => _memory.Write8(rastPort + RastPortOffsets.FgPen, pen);

    // SetBPen(rp, pen)
    //         A1  D0
    [LibraryFunctionOffset(-348)]
    public void SetBPen([A1] uint rastPort, [D0] byte pen) => _memory.Write8(rastPort + RastPortOffsets.BgPen, pen);

    // SetDrMd(rp, drawMode)
    //         A1  D0
    [LibraryFunctionOffset(-354)]
    public void SetDrMd([A1] uint rastPort, [D0] byte mode) => _memory.Write8(rastPort + RastPortOffsets.DrawMode, mode);

    // pos = VBeamPos()
    // D0
    [LibraryFunctionOffset(-384)]
    public int VBeamPos() => core.Chipset.Beam.Line;

    // InitBitMap(bm, depth, width, height)
    //            A0  D0     D1     D2
    [LibraryFunctionOffset(-390)]
    public void InitBitMap([A0] uint bitMap, [D0] byte depth, [D1] ushort width, [D2] ushort height)
    {
        _memory.Write16(bitMap + BitMapOffsets.BytesPerRow, (ushort)(((width + 15) >> 4) << 1));
        _memory.Write16(bitMap + BitMapOffsets.Rows, height);
        _memory.Write8(bitMap + BitMapOffsets.Flags, 0);
        _memory.Write8(bitMap + BitMapOffsets.Depth, depth);
        _memory.Write16(bitMap + 6, 0);
    }

    // ScrollRaster(rp, dx, dy, xMin, yMin, xMax, yMax)
    //              A1  D0  D1  D2    D3    D4    D5
    // Moves the rectangle by (-dx, -dy). The background pen fills the area that the move leaves.
    [LibraryFunctionOffset(-396)]
    public void ScrollRaster([A1] uint rastPort, [D0] short dx, [D1] short dy, [D2] short xMin, [D3] short yMin,
        [D4] short xMax, [D5] short yMax)
    {
        if (_renderer.TargetOf(rastPort) is not { } target)
            return;
        var width = xMax - xMin + 1 - Math.Abs(dx);
        var height = yMax - yMin + 1 - Math.Abs(dy);
        if (width > 0 && height > 0)
        {
            _renderer.Blit(target, xMin + Math.Max(dx, (short)0), yMin + Math.Max(dy, (short)0), target,
                xMin + Math.Max(-dx, 0), yMin + Math.Max(-dy, 0), width, height, 0xC0);
        }

        var background = target with { FgPen = target.BgPen, Mode = DrawMode.Jam1 };
        if (dx > 0)
            _renderer.FillRectangle(background, xMax - dx + 1, yMin, xMax, yMax);
        else if (dx < 0)
            _renderer.FillRectangle(background, xMin, yMin, xMin - dx - 1, yMax);
        if (dy > 0)
            _renderer.FillRectangle(background, xMin, yMax - dy + 1, xMax, yMax);
        else if (dy < 0)
            _renderer.FillRectangle(background, xMin, yMin, xMax, yMin - dy - 1);
    }

    // The runtime blits at once, so the blitter has no owner.
    [LibraryFunctionOffset(-456)]
    public void OwnBlitter()
    {
    }

    [LibraryFunctionOffset(-462)]
    public void DisownBlitter()
    {
    }

    // AskFont(rp, textAttr)
    //         A1  A0
    [LibraryFunctionOffset(-474)]
    public void AskFont([A1] uint rastPort, [A0] uint textAttr)
    {
        var font = _memory.Read32(rastPort + RastPortOffsets.Font);
        _memory.Write32(textAttr + TextAttrOffsets.Name, font == 0 ? 0 : _memory.Read32(font + NodeOffsets.Name));
        _memory.Write16(textAttr + TextAttrOffsets.YSize, _memory.Read16(rastPort + RastPortOffsets.TxHeight));
        _memory.Write8(textAttr + TextAttrOffsets.Style, font == 0 ? (byte)0 : _memory.Read8(font + TextFontOffsets.Style));
        _memory.Write8(textAttr + TextAttrOffsets.Flags, font == 0 ? (byte)0 : _memory.Read8(font + TextFontOffsets.Flags));
    }

    // AddFont(textFont)
    //         A1
    [LibraryFunctionOffset(-480)]
    public void AddFont([A1] uint font) => ExecList.AddTail(_memory, FontList, font);

    // RemFont(textFont)
    //         A1
    [LibraryFunctionOffset(-486)]
    public void RemFont([A1] uint font) => ExecList.Remove(_memory, font);

    // planeptr = AllocRaster(width, height)
    // D0                     D0     D1
    [LibraryFunctionOffset(-492)]
    public uint AllocRaster([D0] ushort width, [D1] ushort height) =>
        core.Allocator.Allocate(RasterSize(width, height), MemoryFlags.Chip);

    // FreeRaster(p, width, height)
    //            A0 D0     D1
    [LibraryFunctionOffset(-498)]
    public void FreeRaster([A0] uint plane, [D0] ushort width, [D1] ushort height) =>
        core.Allocator.Free(plane, RasterSize(width, height));

    // BltBitMapRastPort(srcbm, srcx, srcy, destrp, destX, destY, sizeX, sizeY, minterm)
    //                   A0     D0    D1    A1      D2     D3     D4     D5     D6
    [LibraryFunctionOffset(-606)]
    public void BltBitMapRastPort([A0] uint source, [D0] short sourceX, [D1] short sourceY, [A1] uint rastPort,
        [D2] short destinationX, [D3] short destinationY, [D4] short width, [D5] short height, [D6] byte minterm)
    {
        if (_renderer.TargetOf(rastPort) is not { } to)
            return;
        var from = _renderer.TargetOf(source, 0xFF, 0, 0, DrawMode.Jam2);
        _renderer.Blit(from, sourceX, sourceY, to, destinationX, destinationY, width, height, minterm);
    }

    /// <summary>RASSIZE: the bytes of a plane. Each row is a whole number of words.</summary>
    /// <summary>The bytes of a bit plane of AllocRaster: each row is a whole number of words.</summary>
    public static uint RasterSize(int width, int height) => (uint)(height * (((width + 15) >> 3) & ~1));

    private void ChangeAccessors(uint font, int change)
    {
        var address = font + TextFontOffsets.Accessors;
        _memory.Write16(address, (ushort)Math.Max(0, _memory.Read16(address) + change));
    }

    private readonly record struct GlyphInfo(int Offset, int Width, int Space, int Kern);

    /// <summary>The bitmap location and the spacing of a character. A missing character uses the last glyph of the font.</summary>
    private GlyphInfo Glyph(uint font, byte character)
    {
        var low = _memory.Read8(font + TextFontOffsets.LoChar);
        var high = _memory.Read8(font + TextFontOffsets.HiChar);
        var index = character < low || character > high ? high - low + 1 : character - low;
        var location = _memory.Read32(_memory.Read32(font + TextFontOffsets.CharLoc) + (uint)index * 4);
        var charSpace = _memory.Read32(font + TextFontOffsets.CharSpace);
        var charKern = _memory.Read32(font + TextFontOffsets.CharKern);
        int space = charSpace != 0 ? (short)_memory.Read16(charSpace + (uint)index * 2) : _memory.Read16(font + TextFontOffsets.XSize);
        var kern = charKern != 0 ? (short)_memory.Read16(charKern + (uint)index * 2) : 0;
        return new GlyphInfo((int)(location >> 16), (int)(location & 0xFFFF), space, kern);
    }
}
