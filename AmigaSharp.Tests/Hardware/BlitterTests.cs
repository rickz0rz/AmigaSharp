using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

public class BlitterTests
{
    private const uint Source = 0x1000;
    private const uint Mask = 0x2000;
    private const uint Destination = 0x3000;
    private const int UseA = 0x0800, UseB = 0x0400, UseC = 0x0200, UseD = 0x0100;
    private const byte CopyA = 0xF0;

    private readonly ManualClock _clock = new();
    private readonly Memory _memory = new();
    private readonly Chipset _chipset;

    public BlitterTests()
    {
        _chipset = new Chipset(_clock, _memory);
        _memory.Hardware = _chipset;
        Register(CustomRegister.Dmacon, 0x8240); // DMAEN and BLTEN.
        Register(BlitterRegister.Bltafwm, 0xFFFF);
        Register(BlitterRegister.Bltalwm, 0xFFFF);
    }

    [Fact]
    public void Copy_ShiftsChannelAWithTheBitsOfThePreviousWord()
    {
        Words(Source, 0x1234, 0x5678);
        Register(BlitterRegister.Bltcon0, (4 << 12) | UseA | UseD | CopyA);
        Pointer(BlitterRegister.Bltapt, Source);
        Pointer(BlitterRegister.Bltdpt, Destination);

        Register(BlitterRegister.Bltsize, (1 << 6) | 2);

        Assert.Equal([0x0123, 0x4567], Read(Destination, 2));
    }

    [Fact]
    public void Copy_MasksTheFirstAndTheLastWordOfEachRow_AndAddsTheModulos()
    {
        Words(Source, 0xFFFF, 0xFFFF, 0x0000, 0xFFFF, 0xFFFF);
        Register(BlitterRegister.Bltcon0, UseA | UseD | CopyA);
        Register(BlitterRegister.Bltafwm, 0x0FFF);
        Register(BlitterRegister.Bltalwm, 0xFFF0);
        Register(BlitterRegister.Bltamod, 2);
        Register(BlitterRegister.Bltdmod, 4);
        Pointer(BlitterRegister.Bltapt, Source);
        Pointer(BlitterRegister.Bltdpt, Destination);

        Register(BlitterRegister.Bltsize, (2 << 6) | 2);

        Assert.Equal([0x0FFF, 0xFFF0, 0x0000, 0x0000, 0x0FFF, 0xFFF0], Read(Destination, 6));
        // The pointers keep their values after the blit.
        Assert.Equal(Source + 12, ReadPointer(BlitterRegister.Bltapt));
    }

    [Fact]
    public void CookieCut_UsesTheMaskToChooseTheSourceOrTheBackground()
    {
        Words(Mask, 0xFF00);
        Words(Source, 0xAAAA);
        Words(Destination, 0x5555);
        // D = A ? B : C, the minterm $CA. A is the mask, B the image and C the background.
        Register(BlitterRegister.Bltcon0, UseA | UseB | UseC | UseD | 0xCA);
        Pointer(BlitterRegister.Bltapt, Mask);
        Pointer(BlitterRegister.Bltbpt, Source);
        Pointer(BlitterRegister.Bltcpt, Destination);
        Pointer(BlitterRegister.Bltdpt, Destination);

        Register(BlitterRegister.Bltsize, (1 << 6) | 1);

        Assert.Equal([0xAA55], Read(Destination, 1));
    }

    [Theory]
    [InlineData(0x0A, 0x0FF0)] // Descending and inclusive fill: the edges stay.
    [InlineData(0x12, 0x07F0)] // Descending and exclusive fill: the left edge goes.
    public void Fill_FillsBetweenTheSetBits(ushort control1, ushort expected)
    {
        Words(Source, 0x0810);
        Register(BlitterRegister.Bltcon0, UseA | UseD | CopyA);
        Register(BlitterRegister.Bltcon1, control1);
        Pointer(BlitterRegister.Bltapt, Source);
        Pointer(BlitterRegister.Bltdpt, Destination);

        Register(BlitterRegister.Bltsize, (1 << 6) | 1);

        Assert.Equal([expected], Read(Destination, 1));
    }

    [Fact]
    public void Blit_WaitsForBlitterDma_AndThenSetsZeroFlagAndInterrupt()
    {
        Register(CustomRegister.Dmacon, 0x0040);
        Register(BlitterRegister.Bltcon0, UseD | 0x00);
        Pointer(BlitterRegister.Bltdpt, Destination);
        Words(Destination, 0x1234);

        Register(BlitterRegister.Bltsize, (1 << 6) | 1);
        Assert.Equal([0x1234], Read(Destination, 1));

        Register(CustomRegister.Dmacon, 0x8040);

        Assert.Equal([0x0000], Read(Destination, 1));
        Assert.Equal(0x2000, _memory.Read16(CustomRegister.Base + CustomRegister.Dmaconr) & 0x6000);
        Assert.NotEqual(0, _chipset.Custom.Intreq & (1 << InterruptBit.Blitter));
    }

    [Fact]
    public void Line_DrawsAHorizontalLine()
    {
        const int rowBytes = 40;
        // From (0, 0) to (15, 0): dx = 15, dy = 0. The major axis is x, and the steps go right and down.
        StartLine(Destination, dx: 15, dy: 0, bit: 0, octant: 0);

        Assert.Equal([0xFFFF], Read(Destination, 1));
        Assert.Equal([0x0000], Read(Destination + rowBytes, 1));
    }

    [Fact]
    public void Line_DrawsADiagonalLine()
    {
        const int rowBytes = 40;
        // From (2, 0) to (5, 3).
        StartLine(Destination, dx: 3, dy: 3, bit: 2, octant: 0);

        for (var row = 0; row < 4; row++)
            Assert.Equal([(ushort)(0x2000 >> row)], Read(Destination + (uint)(row * rowBytes), 1));
    }

    private void StartLine(uint start, int dx, int dy, int bit, int octant)
    {
        const int rowBytes = 40;
        var error = 4 * dy - 2 * dx;
        Register(BlitterRegister.Bltcon0, (bit << 12) | UseA | UseC | UseD | 0xCA);
        Register(BlitterRegister.Bltcon1, (octant << 2) | (error < 0 ? 0x40 : 0) | 0x01);
        Register(BlitterRegister.Bltadat, 0x8000);
        Register(BlitterRegister.Bltbdat, 0xFFFF);
        Register(BlitterRegister.Bltapt + 2, (ushort)error);
        Register(BlitterRegister.Bltamod, (ushort)(4 * (dy - dx)));
        Register(BlitterRegister.Bltbmod, (ushort)(4 * dy));
        Register(BlitterRegister.Bltcmod, rowBytes);
        Register(BlitterRegister.Bltdmod, rowBytes);
        Pointer(BlitterRegister.Bltcpt, start);
        Pointer(BlitterRegister.Bltdpt, start);
        Register(BlitterRegister.Bltsize, ((dx + 1) << 6) | 2);
    }

    private void Register(int offset, int value) => _memory.Write16(CustomRegister.Base + (uint)offset, (ushort)value);

    private void Pointer(int offset, uint address)
    {
        Register(offset, (int)(address >> 16));
        Register(offset + 2, (int)(address & 0xFFFF));
    }

    private uint ReadPointer(int offset) => (uint)(_chipset.Custom[offset] << 16 | _chipset.Custom[offset + 2]);

    private void Words(uint address, params ushort[] words)
    {
        for (var i = 0; i < words.Length; i++)
            _memory.Write16(address + (uint)i * 2, words[i]);
    }

    private ushort[] Read(uint address, int count) =>
        Enumerable.Range(0, count).Select(i => _memory.Read16(address + (uint)i * 2)).ToArray();
}
