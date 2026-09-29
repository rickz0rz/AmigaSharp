using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

public class BitBangedLineTests
{
    private const uint CiaBPra = 0xBFD000;
    private const double BitTime = 1.0 / 110;

    private readonly ManualClock _clock = new();
    private readonly Memory _memory = new();
    private readonly Chipset _chipset;

    public BitBangedLineTests()
    {
        _chipset = new Chipset(_clock, _memory);
        _memory.Hardware = _chipset;
    }

    [Fact]
    public void Byte_IsAStartBit_EightDataBitsLowestFirst_AndAStopBit_OnTheCtsPin()
    {
        var line = _chipset.ControlLine;
        line.Connection = new ReplaySerialConnection([0b1000_0101]);

        // The line is high while it has no byte. The byte starts at the first update.
        Assert.True(Cts());
        line.Update();

        bool[] expected = [false, true, false, true, false, false, false, false, true, true];
        for (var bit = 0; bit < expected.Length; bit++)
        {
            _clock.Elapsed = TimeSpan.FromSeconds((bit + 0.5) * BitTime);
            line.Update();
            Assert.Equal(expected[bit], Cts());
        }

        _clock.Elapsed = TimeSpan.FromSeconds(10.5 * BitTime);
        line.Update();
        Assert.True(Cts());
        Assert.Equal(1, line.BytesSent);
    }

    [Fact]
    public void ConsoleLine_UsesTheDsrPin()
    {
        _chipset.ConsoleLine.Connection = new ReplaySerialConnection([0x00]);

        _chipset.ConsoleLine.Update();

        Assert.Equal(0, _memory.Read8(CiaBPra) & 0x08);
        Assert.True(Cts());
    }

    private bool Cts() => (_memory.Read8(CiaBPra) & 0x10) != 0;
}
