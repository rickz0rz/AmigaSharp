using AmigaSharp.Runtime.Graphics;
using AmigaSharp.Tests.Libraries;

namespace AmigaSharp.Tests.Graphics;

/// <summary>Tests with the font in samples/Fonts: "test.font" size 9, with proportional characters.</summary>
public sealed class DiskFontTests : IDisposable
{
    private const short OpenDiskFont = -30;
    private const short TextLength = -54;
    private const short Text = -60;
    private const short SetFont = -66;
    private const short OpenFont = -72;
    private const short InitRastPort = -198;
    private const short Move = -240;
    private const short SetAPen = -342;
    private const short SetDrMd = -354;
    private const short InitBitMap = -390;
    private const short AllocRaster = -492;

    private readonly LibraryHarness _harness = new();
    private readonly uint _graphics;
    private readonly uint _diskFont;

    public DiskFontTests()
    {
        _harness.Core.FileSystem.AddVolume("FONTS", Path.Combine(TestPaths.RepositoryRoot, "samples", "Fonts"));
        _graphics = _harness.Core.OpenLibrary("graphics.library", 0)!.Base;
        _diskFont = _harness.Core.OpenLibrary("diskfont.library", 0)!.Base;
    }

    [Fact]
    public void OpenDiskFont_LoadsTheFont_AndOpenFontFindsItAfterThat()
    {
        var font = _harness.Call(_diskFont, OpenDiskFont, ("A0", TextAttr("test.font", 9)));

        Assert.NotEqual(0u, font);
        Assert.Equal(9, _harness.Memory.Read16(font + TextFontOffsets.YSize));
        Assert.Equal(1, _harness.Memory.Read16(font + TextFontOffsets.Accessors));
        Assert.Equal("test.font", _harness.Memory.ReadCString(_harness.Memory.Read32(font + 10)));
        Assert.Equal(font, _harness.Call(_graphics, OpenFont, ("A0", TextAttr("test.font", 9))));
    }

    [Fact]
    public void OpenDiskFont_MissingFont_FallsBackToAFontInMemory()
    {
        var topaz = _harness.Memory.Read32(_graphics + GfxBaseOffsets.DefaultFont);

        Assert.Equal(topaz, _harness.Call(_diskFont, OpenDiskFont, ("A0", TextAttr("topaz.font", 11))));
        Assert.Equal(0u, _harness.Call(_diskFont, OpenDiskFont, ("A0", TextAttr("nosuch.font", 8))));
    }

    [Fact]
    public void Text_UsesTheWidthsTheSpacesAndTheKerns()
    {
        var font = _harness.Call(_diskFont, OpenDiskFont, ("A0", TextAttr("test.font", 9)));
        var (rastPort, bitMap) = NewRastPort();
        _harness.Call(_graphics, SetFont, ("A1", rastPort), ("A0", font));
        _harness.Call(_graphics, SetAPen, ("A1", rastPort), ("D0", 1));
        _harness.Call(_graphics, SetDrMd, ("A1", rastPort), ("D0", 0));
        _harness.Call(_graphics, Move, ("A1", rastPort), ("D0", 0), ("D1", 7));
        var text = _harness.String("ABCZ");

        _harness.Call(_graphics, Text, ("A1", rastPort), ("A0", text), ("D0", 4));

        // A is at 0 (space 6). B has a kern of 1, so it is at 7 (space 5). C is at 11 (space 4). Z is not in the
        // font, so the box is at 15.
        Assert.Equal(
            "..#....###..##.####..\n" +
            ".#.#...#..##...#..#..\n" +
            "#...#..###.#...#..#..\n" +
            "#...#..#..##...#..#..\n" +
            "#####..#..##...#..#..\n" +
            "#...#..###..##.####..\n" +
            "#...#................\n",
            PlanarImage.ToText(PlanarImage.ReadPens(_harness.Memory, bitMap), 0, 0, 21, 7).Replace('1', '#'));
        Assert.Equal(21u, _harness.Call(_graphics, TextLength, ("A1", rastPort), ("A0", text), ("D0", 4)));
    }

    private uint TextAttr(string name, int ySize)
    {
        var attr = _harness.Core.AllocateSystem(TextAttrOffsets.Size);
        _harness.Memory.Write32(attr + TextAttrOffsets.Name, _harness.String(name));
        _harness.Memory.Write16(attr + TextAttrOffsets.YSize, (ushort)ySize);
        return attr;
    }

    private (uint RastPort, uint BitMap) NewRastPort()
    {
        var bitMap = _harness.Core.AllocateSystem(BitMapOffsets.Size);
        _harness.Call(_graphics, InitBitMap, ("A0", bitMap), ("D0", 1), ("D1", 32), ("D2", 9));
        _harness.Memory.Write32(bitMap + BitMapOffsets.Planes, _harness.Call(_graphics, AllocRaster, ("D0", 32), ("D1", 9)));
        var rastPort = _harness.Core.AllocateSystem(RastPortOffsets.Size);
        _harness.Call(_graphics, InitRastPort, ("A1", rastPort));
        _harness.Memory.Write32(rastPort + RastPortOffsets.BitMap, bitMap);
        return (rastPort, bitMap);
    }

    public void Dispose() => _harness.Dispose();
}
