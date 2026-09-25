using System.Text;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Graphics;
using AmigaSharp.Tests.Libraries;

namespace AmigaSharp.Tests.Graphics;

public sealed class GraphicsLibraryTests : IDisposable
{
    private const short BltBitMap = -30;
    private const short TextLength = -54;
    private const short Text = -60;
    private const short SetFont = -66;
    private const short OpenFont = -72;
    private const short InitRastPort = -198;
    private const short SetRast = -234;
    private const short Move = -240;
    private const short Draw = -246;
    private const short BltClear = -300;
    private const short RectFill = -306;
    private const short ReadPixel = -318;
    private const short WritePixel = -324;
    private const short SetAPen = -342;
    private const short SetBPen = -348;
    private const short SetDrMd = -354;
    private const short InitBitMap = -390;
    private const short ScrollRaster = -396;
    private const short AllocRaster = -492;
    private const short BltBitMapRastPort = -606;

    private const int Width = 64;
    private const int Height = 16;

    private readonly LibraryHarness _harness = new();
    private readonly uint _graphics;
    private readonly uint _bitMap;
    private readonly uint _rastPort;

    public GraphicsLibraryTests()
    {
        _graphics = _harness.Core.OpenLibrary("graphics.library", 0)!.Base;
        _bitMap = NewBitMap(depth: 2);
        _rastPort = _harness.Core.AllocateSystem(RastPortOffsets.Size);
        Call(InitRastPort, ("A1", _rastPort));
        _harness.Memory.Write32(_rastPort + RastPortOffsets.BitMap, _bitMap);
    }

    [Fact]
    public void InitRastPort_SetsTheDefaults()
    {
        var memory = _harness.Memory;

        Assert.Equal(0xFF, memory.Read8(_rastPort + RastPortOffsets.Mask));
        Assert.Equal(0xFF, memory.Read8(_rastPort + RastPortOffsets.FgPen));
        Assert.Equal((byte)DrawMode.Jam2, memory.Read8(_rastPort + RastPortOffsets.DrawMode));
        Assert.Equal(0xFFFF, memory.Read16(_rastPort + RastPortOffsets.LinePattern));
        Assert.Equal(DefaultFont, memory.Read32(_rastPort + RastPortOffsets.Font));
        Assert.Equal(8, memory.Read16(_rastPort + RastPortOffsets.TxHeight));
    }

    [Fact]
    public void InitBitMap_RoundsTheRowsToWords()
    {
        var memory = _harness.Memory;
        var bitMap = _harness.Core.AllocateSystem(BitMapOffsets.Size);

        Call(InitBitMap, ("A0", bitMap), ("D0", 3), ("D1", 20), ("D2", 7));

        Assert.Equal(4, memory.Read16(bitMap + BitMapOffsets.BytesPerRow));
        Assert.Equal(7, memory.Read16(bitMap + BitMapOffsets.Rows));
        Assert.Equal(3, memory.Read8(bitMap + BitMapOffsets.Depth));
    }

    [Fact]
    public void RectFill_FillsBothCorners_WithThePen()
    {
        Pen(3);

        Call(RectFill, ("A1", _rastPort), ("D0", 2), ("D1", 1), ("D2", 4), ("D3", 2));

        Assert.Equal(
            "......\n" +
            "..333.\n" +
            "..333.\n" +
            "......\n", Area(0, 0, 6, 4));
    }

    [Fact]
    public void RectFill_Complement_InvertsTheBits()
    {
        Pen(1);
        Call(RectFill, ("A1", _rastPort), ("D0", 0), ("D1", 0), ("D2", 3), ("D3", 0));
        Call(SetDrMd, ("A1", _rastPort), ("D0", (uint)DrawMode.Complement));

        Call(RectFill, ("A1", _rastPort), ("D0", 2), ("D1", 0), ("D2", 5), ("D3", 0));

        Assert.Equal("112233\n", Area(0, 0, 6, 1));
    }

    [Fact]
    public void WriteMask_ProtectsPlanes()
    {
        _harness.Memory.Write8(_rastPort + RastPortOffsets.Mask, 0b01);
        Pen(3);

        Call(RectFill, ("A1", _rastPort), ("D0", 0), ("D1", 0), ("D2", 1), ("D3", 0));

        Assert.Equal("11\n", Area(0, 0, 2, 1));
    }

    [Fact]
    public void Draw_DrawsBothEnds_AndMovesThePosition()
    {
        Pen(2);
        Call(Move, ("A1", _rastPort), ("D0", 0), ("D1", 0));

        Call(Draw, ("A1", _rastPort), ("D0", 3), ("D1", 3));
        Call(Draw, ("A1", _rastPort), ("D0", 6), ("D1", 3));

        Assert.Equal(
            "2......\n" +
            ".2.....\n" +
            "..2....\n" +
            "...2222\n", Area(0, 0, 7, 4));
        Assert.Equal(6, _harness.Memory.Read16(_rastPort + RastPortOffsets.X));
        Assert.Equal(3, _harness.Memory.Read16(_rastPort + RastPortOffsets.Y));
    }

    [Fact]
    public void ReadPixelAndWritePixel_UseTheForegroundPen()
    {
        Pen(2);

        Assert.Equal(0u, Call(WritePixel, ("A1", _rastPort), ("D0", 5), ("D1", 6)));
        Assert.Equal(2u, Call(ReadPixel, ("A1", _rastPort), ("D0", 5), ("D1", 6)));
        Assert.Equal(unchecked((uint)-1), Call(ReadPixel, ("A1", _rastPort), ("D0", Width), ("D1", 0)));
    }

    [Fact]
    public void DrawingOutsideTheBitmap_IsClipped()
    {
        Pen(1);

        Call(RectFill, ("A1", _rastPort), ("D0", unchecked((uint)-5)), ("D1", unchecked((uint)-5)), ("D2", 1), ("D3", 1));

        Assert.Equal("11.\n11.\n...\n", Area(0, 0, 3, 3));
    }

    [Fact]
    public void SetRast_FillsTheBitmap()
    {
        Call(SetRast, ("A1", _rastPort), ("D0", 2));

        Assert.Equal("22\n22\n", Area(Width - 2, Height - 2, 2, 2));
    }

    [Fact]
    public void Text_Jam2_DrawsTheGlyphAndTheBackground()
    {
        Pen(1);
        Call(SetBPen, ("A1", _rastPort), ("D0", 2));
        Call(Move, ("A1", _rastPort), ("D0", 0), ("D1", BuiltInFont.Baseline));

        Call(Text, ("A1", _rastPort), ("A0", _harness.String("A")), ("D0", 1));

        Assert.Equal(GlyphText('A', set: '1', clear: '2'), Area(0, 0, 8, 8));
        Assert.Equal(8, _harness.Memory.Read16(_rastPort + RastPortOffsets.X));
    }

    [Fact]
    public void Text_Jam1_DrawsOnlyTheGlyph()
    {
        Pen(3);
        Call(SetDrMd, ("A1", _rastPort), ("D0", (uint)DrawMode.Jam1));
        Call(Move, ("A1", _rastPort), ("D0", 8), ("D1", BuiltInFont.Baseline));

        Call(Text, ("A1", _rastPort), ("A0", _harness.String("xB")), ("D0", 2));

        Assert.Equal(GlyphText('B', set: '3', clear: '.'), Area(16, 0, 8, 8));
    }

    [Fact]
    public void TextLength_OfTheBuiltInFont_IsEightForEachCharacter()
    {
        Assert.Equal(40u, Call(TextLength, ("A1", _rastPort), ("A0", _harness.String("Hello")), ("D0", 5)));
    }

    [Fact]
    public void OpenFont_FindsTopaz_AtTheNearestSize()
    {
        var name = _harness.String("topaz.font");
        var textAttr = _harness.Core.AllocateSystem(TextAttrOffsets.Size);
        _harness.Memory.Write32(textAttr + TextAttrOffsets.Name, name);
        _harness.Memory.Write16(textAttr + TextAttrOffsets.YSize, 9);

        Assert.Equal(DefaultFont, Call(OpenFont, ("A0", textAttr)));

        _harness.Memory.Write32(textAttr + TextAttrOffsets.Name, _harness.String("nosuch.font"));
        Assert.Equal(0u, Call(OpenFont, ("A0", textAttr)));
    }

    [Fact]
    public void BltBitMapRastPort_CopiesAndCombines()
    {
        Pen(1);
        Call(RectFill, ("A1", _rastPort), ("D0", 0), ("D1", 0), ("D2", 1), ("D3", 0));
        Pen(3);
        Call(RectFill, ("A1", _rastPort), ("D0", 11), ("D1", 0), ("D2", 11), ("D3", 0));

        // $C0 copies the source, and $60 is the source XOR the destination.
        Call(BltBitMapRastPort, ("A0", _bitMap), ("D0", 0), ("D1", 0), ("A1", _rastPort), ("D2", 5), ("D3", 0),
            ("D4", 2), ("D5", 1), ("D6", 0xC0));
        Call(BltBitMapRastPort, ("A0", _bitMap), ("D0", 0), ("D1", 0), ("A1", _rastPort), ("D2", 10), ("D3", 0),
            ("D4", 2), ("D5", 1), ("D6", 0x60));

        Assert.Equal("11...11...12\n", Area(0, 0, 12, 1));
    }

    [Fact]
    public void BltBitMap_UsesThePlaneMask()
    {
        var target = NewBitMap(depth: 2);
        Pen(3);
        Call(RectFill, ("A1", _rastPort), ("D0", 0), ("D1", 0), ("D2", 0), ("D3", 0));

        var planes = Call(BltBitMap, ("A0", _bitMap), ("D0", 0), ("D1", 0), ("A1", target), ("D2", 0), ("D3", 0),
            ("D4", 1), ("D5", 1), ("D6", 0xC0), ("D7", 0b10), ("A2", 0));

        Assert.Equal(2u, planes);
        Assert.Equal(2, PlanarImage.ReadPens(_harness.Memory, target)[0, 0]);
    }

    [Fact]
    public void ScrollRaster_MovesTheArea_AndClearsWithTheBackgroundPen()
    {
        Pen(1);
        Call(RectFill, ("A1", _rastPort), ("D0", 2), ("D1", 0), ("D2", 3), ("D3", 0));
        Call(SetBPen, ("A1", _rastPort), ("D0", 2));

        Call(ScrollRaster, ("A1", _rastPort), ("D0", 2), ("D1", 0), ("D2", 0), ("D3", 0), ("D4", 5), ("D5", 0));

        Assert.Equal("11..22\n", Area(0, 0, 6, 1));
    }

    [Fact]
    public void BltClear_WithRowsAndBytes_ClearsTheBlock()
    {
        var block = _harness.Core.AllocateSystem(8);
        _harness.Memory.WriteBytes(block, [1, 2, 3, 4, 5, 6, 7, 8]);

        Call(BltClear, ("A1", block), ("D0", (2u << 16) | 3), ("D1", 2));

        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 7, 8 }, _harness.Memory.ReadBytes(block, 8));
    }

    [Fact]
    public void AllocRaster_GivesChipMemory()
    {
        var plane = Call(AllocRaster, ("D0", 320), ("D1", 200));

        Assert.True(_harness.Core.Allocator.TypeOf(plane).HasFlag(MemoryFlags.Chip));
    }

    private uint DefaultFont => _harness.Memory.Read32(_graphics + GfxBaseOffsets.DefaultFont);

    private uint Call(short offset, params (string, uint)[] registers) => _harness.Call(_graphics, offset, registers);

    private void Pen(uint pen) => Call(SetAPen, ("A1", _rastPort), ("D0", pen));

    private uint NewBitMap(int depth)
    {
        var bitMap = _harness.Core.AllocateSystem(BitMapOffsets.Size);
        Call(InitBitMap, ("A0", bitMap), ("D0", (uint)depth), ("D1", Width), ("D2", Height));
        for (var plane = 0; plane < depth; plane++)
            _harness.Memory.Write32(bitMap + BitMapOffsets.Planes + (uint)plane * 4, Call(AllocRaster, ("D0", Width), ("D1", Height)));
        return bitMap;
    }

    private string Area(int x, int y, int width, int height) =>
        PlanarImage.ToText(PlanarImage.ReadPens(_harness.Memory, _bitMap), x, y, width, height);

    private static string GlyphText(char character, char set, char clear)
    {
        var glyph = BuiltInFont.Glyphs.Slice((character - BuiltInFont.FirstCharacter) * BuiltInFont.Height, BuiltInFont.Height);
        var text = new StringBuilder();
        foreach (var row in glyph)
        {
            for (var bit = 7; bit >= 0; bit--)
                text.Append((row & (1 << bit)) != 0 ? set : clear);
            text.Append('\n');
        }

        return text.ToString();
    }

    public void Dispose() => _harness.Dispose();
}
