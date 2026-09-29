using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

/// <summary>Tests of the copper and the display with copper lists and bitplanes in chip memory.</summary>
public class DisplayTests
{
    private const uint CopperList = 0x1_0000;
    private const uint SecondCopperList = 0x1_8000;
    private const uint Plane = 0x2_0000;
    private const uint Red = 0xFFFF_0000;
    private const uint Green = 0xFF00_FF00;
    private const uint Black = 0xFF00_0000;
    private const uint Blue = 0xFF00_00FF;

    // The first pixel of a standard display: low-resolution pixel $81, at DDFSTRT $38.
    private const int FirstX = (0x81 - Display.FirstLowResolutionPixel) * 2;

    private readonly ManualClock _clock = new();
    private readonly Memory _memory = new();
    private readonly Chipset _chipset;

    public DisplayTests()
    {
        _chipset = new Chipset(_clock, _memory);
        _memory.Hardware = _chipset;
    }

    [Fact]
    public void LowResolution_ShowsTheFirstPixelAtTheStartOfTheWindow()
    {
        SetUpLowResolution();
        _memory.Write8(Plane, 0x80);

        var frame = NextFrame();

        Assert.Equal(Red, Pixel(frame, FirstX, 0x2C));
        Assert.Equal(Red, Pixel(frame, FirstX + 1, 0x2C));
        Assert.Equal(Black, Pixel(frame, FirstX + 2, 0x2C));
        // Outside the window: the background color.
        Assert.Equal(Black, Pixel(frame, FirstX - 2, 0x2C));
    }

    [Fact]
    public void EachLine_UsesTheNextRowOfThePlane_AndTheModulo()
    {
        SetUpLowResolution();
        Write(0xDFF108, 40); // BPL1MOD: skip one row of 40 bytes after each line.
        _memory.Write8(Plane, 0x80);
        _memory.Write8(Plane + 80, 0x40);

        var frame = NextFrame();

        Assert.Equal(Red, Pixel(frame, FirstX, 0x2C));
        Assert.Equal(Red, Pixel(frame, FirstX + 2, 0x2D));
        Assert.Equal(Black, Pixel(frame, FirstX, 0x2D));
    }

    [Fact]
    public void PlanePointerWrite_AfterDdfstop_IsTheStartOfTheNextLine()
    {
        // At the end of line $2C, after DDFSTOP ($D0), the copper points plane 1 to a new row. The display must
        // not add the modulo to that value.
        const uint newRow = Plane + 0x1000;
        SetUpLowResolution(
            Wait(0x2C, 0xD4),
            Move(0x0E0, newRow >> 16),
            Move(0x0E2, newRow & 0xFFFF));
        Write(0xDFF108, 40); // BPL1MOD
        _memory.Write8(Plane + 80, 0x80); // The row that the modulo gives.
        _memory.Write8(newRow, 0x40);

        var frame = NextFrame();

        Assert.Equal(Black, Pixel(frame, FirstX, 0x2D));
        Assert.Equal(Red, Pixel(frame, FirstX + 2, 0x2D));
    }

    [Fact]
    public void HighResolution_DdfstopThatNoFetchUnitMatches_FetchesToTheHardwareLimit()
    {
        // Prevue uses DDFSTRT $28 and DDFSTOP $D6. The units start at $28, $2C, ... so none is $D6, and the fetch
        // continues to $D8: 46 words, 92 bytes. With a modulo of 0, the next line starts 92 bytes later.
        SetUpLowResolution();
        Write(0xDFF100, 0x9200); // BPLCON0: high resolution, 1 plane.
        Write(0xDFF092, 0x28);
        Write(0xDFF094, 0xD6);
        _memory.Write8(Plane + 92 + 16, 0x80);

        var frame = NextFrame();

        // The first data pixel is at ($28 * 2 + 9 - $48) * 2 = 34, and byte 16 starts 128 pixels later.
        Assert.Equal(Red, Pixel(frame, 34 + 128, 0x2D));
    }

    [Fact]
    public void CopperWait_ChangesAColorFromALine()
    {
        SetUpLowResolution(
            Wait(100, 0x00),
            Move(0x180, 0x00F));

        var frame = NextFrame();

        Assert.Equal(Black, Pixel(frame, 10, 99));
        Assert.Equal(Blue, Pixel(frame, 10, 100));
    }

    [Fact]
    public void CopperWait_ChangesAColorInTheMiddleOfALine()
    {
        SetUpLowResolution(
            Wait(100, 0x80),
            Move(0x180, 0x00F));

        var frame = NextFrame();

        // Color clock $80 is low-resolution pixel $100.
        var changeX = (0x100 - Display.FirstLowResolutionPixel) * 2;
        Assert.Equal(Black, Pixel(frame, changeX - 2, 100));
        Assert.Equal(Blue, Pixel(frame, changeX, 100));
    }

    [Fact]
    public void HighResolution_ShowsEachBitAsOnePixel()
    {
        SetUpLowResolution();
        Write(0xDFF100, 0x9200); // BPLCON0: high resolution, 1 plane.
        Write(0xDFF092, 0x3C);
        Write(0xDFF094, 0xD4);
        _memory.Write8(Plane, 0xA0);

        var frame = NextFrame();

        Assert.Equal(Red, Pixel(frame, FirstX, 0x2C));
        Assert.Equal(Black, Pixel(frame, FirstX + 1, 0x2C));
        Assert.Equal(Red, Pixel(frame, FirstX + 2, 0x2C));
    }

    [Fact]
    public void TwoPlanes_SelectFourColors()
    {
        SetUpLowResolution(Move(0x184, 0x0F0), Move(0x186, 0x00F));
        Write(0xDFF100, 0x2200); // BPLCON0: 2 planes.
        const uint secondPlane = Plane + 0x4000;
        Write32(0xDFF0E4, secondPlane);
        _memory.Write8(Plane, 0b1010_0000);
        _memory.Write8(secondPlane, 0b0110_0000);

        var frame = NextFrame();

        Assert.Equal(Red, Pixel(frame, FirstX + 0, 0x2C)); // pen 1
        Assert.Equal(Green, Pixel(frame, FirstX + 2, 0x2C)); // pen 2
        Assert.Equal(Blue, Pixel(frame, FirstX + 4, 0x2C)); // pen 3
        Assert.Equal(Black, Pixel(frame, FirstX + 6, 0x2C)); // pen 0
    }

    [Fact]
    public void Interlace_AlternatesTheLongFrameFlag_AndTheRows()
    {
        SetUpLowResolution();
        Write(0xDFF100, 0x1204); // BPLCON0: 1 plane, interlace.
        _memory.Write8(Plane, 0x80);

        var first = NextFrame();
        Assert.Equal(0, _memory.Read16(0xDFF004) & 0x8000);
        var second = NextFrame();
        Assert.NotEqual(0, _memory.Read16(0xDFF004) & 0x8000);

        // The long frame fills the even rows, and the short frame fills the odd rows.
        var row = (0x2C - Display.FirstLine) * 2;
        Assert.Equal(Red, first[row * Display.Width + FirstX]);
        Assert.Equal(Red, second[(row + 1) * Display.Width + FirstX]);
    }

    [Theory]
    [InlineData(DeinterlaceMode.Weave, Red, Black)]
    [InlineData(DeinterlaceMode.Bob, Black, Black)]
    [InlineData(DeinterlaceMode.Blend, 0xFF7F_0000u, 0xFF7F_0000u)]
    public void Deinterlace_ShowsTheTwoFields_AsTheModeSays(DeinterlaceMode mode, uint evenRow, uint oddRow)
    {
        // The long field (even rows) has a red pixel. The short field (odd rows) that follows it has none.
        SetUpLowResolution();
        Write(0xDFF100, 0x1204); // BPLCON0: 1 plane, interlace.
        _chipset.Display.Deinterlace = mode;
        _memory.Write8(Plane, 0x80);
        NextFrame();
        _memory.Write8(Plane, 0x00);

        var frame = NextFrame();

        var row = (0x2C - Display.FirstLine) * 2;
        Assert.Equal(evenRow, frame[row * Display.Width + FirstX]);
        Assert.Equal(oddRow, frame[(row + 1) * Display.Width + FirstX]);
    }

    [Fact]
    public void CopperList_CanSetTheListOfTheNextFrame()
    {
        WriteCopperList(CopperList, Move(0x180, 0xF00), Move(0x080, SecondCopperList >> 16), Move(0x082, SecondCopperList & 0xFFFF), End());
        WriteCopperList(SecondCopperList, Move(0x180, 0x0F0), Move(0x080, CopperList >> 16), Move(0x082, CopperList & 0xFFFF), End());
        Write32(0xDFF080, CopperList);
        Write(0xDFF096, 0x8280);

        Assert.Equal(Red, Pixel(NextFrame(), 0, 50));
        Assert.Equal(Green, Pixel(NextFrame(), 0, 50));
        Assert.Equal(Red, Pixel(NextFrame(), 0, 50));
    }

    [Fact]
    public void CopperSkip_SkipsTheNextInstruction_AfterThePosition()
    {
        SetUpLowResolution(
            Wait(60, 0),
            Skip(50, 0),
            Move(0x180, 0x0F0),
            Move(0x182, 0x00F));

        var frame = NextFrame();

        Assert.Equal(Black, Pixel(frame, 0, 70));
    }

    [Fact]
    public void NoCopperDma_ShowsTheColorsThatTheCpuWrote()
    {
        Write(0xDFF180, 0x00F);
        Write(0xDFF096, 0x8200);

        Assert.Equal(Blue, Pixel(NextFrame(), 100, 100));
    }

    [Fact]
    public void Sprite_ShowsItsLinesAtItsPosition_AndTheChannelShowsTheNextSprite()
    {
        const uint sprite = 0x3_0000;
        // HSTART $80 puts the first pixel at the start of the window, $81.
        Words(sprite,
            0x3040, 0x3200, 0x8000, 0x0000, 0x0000, 0x8000, // Lines $30 and $31.
            0x4040, 0x4100, 0x8000, 0x0000, // Line $40.
            0x0000, 0x0000);
        SetUpLowResolution(Move(0x120, sprite >> 16), Move(0x122, sprite & 0xFFFF), Move(0x1A2, 0x0F0), Move(0x1A4, 0x00F));
        Write(0xDFF096, 0x8020); // DMACON: sprites

        NextFrame();
        var frame = NextFrame();

        Assert.Equal(Green, Pixel(frame, FirstX, 0x30));
        Assert.Equal(Black, Pixel(frame, FirstX + 2, 0x30));
        Assert.Equal(Blue, Pixel(frame, FirstX, 0x31));
        Assert.Equal(Black, Pixel(frame, FirstX, 0x32));
        Assert.Equal(Green, Pixel(frame, FirstX, 0x40));
    }

    [Theory]
    [InlineData(0x00, Red)] // The playfield is in front of all sprites.
    [InlineData(0x24, Green)] // All sprites are in front of the playfields.
    public void Sprite_UsesThePriorityOfBplcon2(ushort bplcon2, uint expected)
    {
        const uint sprite = 0x3_0000;
        Words(sprite, 0x3040, 0x3100, 0x8000, 0x0000, 0x0000, 0x0000);
        _memory.Write8(Plane + (0x30 - 0x2C) * 40, 0x80);
        SetUpLowResolution(Move(0x120, sprite >> 16), Move(0x122, sprite & 0xFFFF), Move(0x1A2, 0x0F0),
            Move(0x104, bplcon2));
        Write(0xDFF096, 0x8020);

        NextFrame();

        Assert.Equal(expected, Pixel(NextFrame(), FirstX, 0x30));
    }

    [Fact]
    public void AttachedSprites_MakeOneSpriteWith15Colors()
    {
        const uint even = 0x3_0000, odd = 0x3_1000;
        Words(even, 0x3040, 0x3100, 0x8000, 0x0000, 0x0000, 0x0000);
        Words(odd, 0x3040, 0x3180, 0x8000, 0x0000, 0x0000, 0x0000);
        // Bit 0 from the even sprite and bit 2 from the odd sprite: color 16 + 5.
        SetUpLowResolution(Move(0x120, even >> 16), Move(0x122, even & 0xFFFF), Move(0x124, odd >> 16),
            Move(0x126, odd & 0xFFFF), Move(0x1AA, 0x00F));
        Write(0xDFF096, 0x8020);

        NextFrame();

        Assert.Equal(Blue, Pixel(NextFrame(), FirstX, 0x30));
    }

    private void Words(uint address, params ushort[] words)
    {
        for (var i = 0; i < words.Length; i++)
            _memory.Write16(address + (uint)i * 2, words[i]);
    }

    /// <summary>A display of 320 by 200 low-resolution pixels with 1 plane at <see cref="Plane"/>, color 1 red.</summary>
    private void SetUpLowResolution(params uint[][] extra)
    {
        Write(0xDFF08E, 0x2C81); // DIWSTRT
        Write(0xDFF090, 0xF4C1); // DIWSTOP
        Write(0xDFF092, 0x38); // DDFSTRT
        Write(0xDFF094, 0xD0); // DDFSTOP
        Write(0xDFF100, 0x1200); // BPLCON0: 1 plane
        var list = new List<uint[]>
        {
            Move(0x0E0, Plane >> 16),
            Move(0x0E2, Plane & 0xFFFF),
            Move(0x180, 0x000),
            Move(0x182, 0xF00),
        };
        list.AddRange(extra);
        list.Add(End());
        WriteCopperList(CopperList, list.ToArray());
        Write32(0xDFF080, CopperList);
        Write(0xDFF096, 0x8380); // DMACON: DMA, bitplanes, copper
    }

    private uint[] NextFrame()
    {
        _clock.Advance(TimeSpan.FromSeconds(1.0 / 59));
        _chipset.Custom.Update();
        var frame = new uint[Display.Width * Display.Height];
        _chipset.Display.CopyFrame(frame);
        return frame;
    }

    private static uint Pixel(uint[] frame, int x, int line) =>
        frame[(line - Display.FirstLine) * 2 * Display.Width + x];

    private void WriteCopperList(uint address, params uint[][] instructions)
    {
        foreach (var instruction in instructions)
        {
            _memory.Write16(address, (ushort)instruction[0]);
            _memory.Write16(address + 2, (ushort)instruction[1]);
            address += 4;
        }
    }

    private static uint[] Move(int register, uint value) => [(uint)register, value];

    private static uint[] Wait(int line, int horizontal) => [(uint)(line << 8 | horizontal | 1), 0xFFFE];

    private static uint[] Skip(int line, int horizontal) => [(uint)(line << 8 | horizontal | 1), 0xFFFF];

    private static uint[] End() => [0xFFFF, 0xFFFE];

    private void Write(uint address, ushort value) => _memory.Write16(address, value);

    private void Write32(uint address, uint value) => _memory.Write32(address, value);
}
