using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

/// <summary>
/// A chipset with chip memory for tests of the copper and the display, and the helpers of these tests: copper
/// instructions, the next frame, and the pixels of the picture.
/// </summary>
public abstract class DisplayTestBench
{
    protected const uint Red = 0xFFFF_0000;
    protected const uint Green = 0xFF00_FF00;
    protected const uint Black = 0xFF00_0000;
    protected const uint Blue = 0xFF00_00FF;

    /// <param name="aga">True for the AGA chipset, false for ECS.</param>
    protected DisplayTestBench(bool aga = false)
    {
        Chipset = new Chipset(Clock, Memory, aga: aga);
        Memory.Hardware = Chipset;
    }

    protected ManualClock Clock { get; } = new();
    protected Memory Memory { get; } = new();
    protected Chipset Chipset { get; }

    /// <summary>Lets the next frame end, and returns the picture.</summary>
    protected uint[] NextFrame()
    {
        Clock.Advance(TimeSpan.FromSeconds(1.0 / 59));
        Chipset.Custom.Update();
        var frame = new uint[Display.Width * Display.HeightOf(VideoStandard.Ntsc)];
        Chipset.Display.CopyFrame(frame);
        return frame;
    }

    /// <summary>The x in the picture of a low-resolution pixel.</summary>
    protected static int X(int lowResolution) => (lowResolution - Display.FirstLowResolutionPixel) * 2;

    /// <summary>The color of a pixel, without its genlock key.</summary>
    protected static uint Pixel(uint[] frame, int x, int line) =>
        frame[(line - VideoStandard.Ntsc.FirstLine) * 2 * Display.Width + x] | 0xFF00_0000;

    /// <summary>True if the pixel is the genlock key: its alpha is 0.</summary>
    protected static bool IsKey(uint[] frame, int x, int line) =>
        frame[(line - VideoStandard.Ntsc.FirstLine) * 2 * Display.Width + x] >> 24 == 0;

    protected void WriteCopperList(uint address, params uint[][] instructions)
    {
        foreach (var instruction in instructions)
        {
            Memory.Write16(address, (ushort)instruction[0]);
            Memory.Write16(address + 2, (ushort)instruction[1]);
            address += 4;
        }
    }

    protected static uint[] Move(int register, uint value) => [(uint)register, value];

    protected static uint[] Wait(int line, int horizontal) => [(uint)(line << 8 | horizontal | 1), 0xFFFE];

    protected static uint[] Skip(int line, int horizontal) => [(uint)(line << 8 | horizontal | 1), 0xFFFF];

    protected static uint[] End() => [0xFFFF, 0xFFFE];

    protected void Write(uint address, ushort value) => Memory.Write16(address, value);

    protected void Write32(uint address, uint value) => Memory.Write32(address, value);
}
