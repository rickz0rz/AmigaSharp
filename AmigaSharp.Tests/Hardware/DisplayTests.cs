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
