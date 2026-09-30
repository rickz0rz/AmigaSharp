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
        var line = _chipset.CtsLine;
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
    public void Line_KeepsItsBytes_UntilTheProgramIsReady()
    {
        var ready = false;
        var line = _chipset.CtsLine;
        line.Connection = new ReplaySerialConnection([0x00]);
        line.Ready = () => ready;

        line.Update();
        Assert.True(Cts());
        Assert.Equal(0, line.BytesSent);

        ready = true;
        line.Update();
        Assert.False(Cts());
        Assert.Equal(1, line.BytesSent);
    }

    [Fact]
    public void AudioSampleTime_GoesOneIntervalForEachRequest_AlsoWhenTheRuntimeIsLate()
    {
        const int intreq = 0xDFF09C, aud1 = 1 << 8;
        const double interval = 2 * 0x65B / Beam.ColorClockHz;
        _memory.Write16(0xDFF0B4, 1);
        _memory.Write16(0xDFF0B6, 0x65B);
        _memory.Write16(0xDFF096, 0x8202);
        _chipset.Custom.Update();
        Assert.Equal(0, _chipset.Custom.AudioSampleTime(1).TotalSeconds);

        // The runtime is 20 ms late, but the program gets only one more interrupt now.
        _memory.Write16(intreq, aud1);
        _clock.Elapsed = TimeSpan.FromMilliseconds(20);
        _chipset.Custom.Update();
        Assert.Equal(interval, _chipset.Custom.AudioSampleTime(1).TotalSeconds, 6);

        _memory.Write16(intreq, aud1);
        _chipset.Custom.Update();
        Assert.Equal(2 * interval, _chipset.Custom.AudioSampleTime(1).TotalSeconds, 6);
    }

    [Fact]
    public void Line_UsesItsTime_AndTheTimeNeverGoesBack()
    {
        var time = TimeSpan.Zero;
        var line = _chipset.CtsLine;
        line.Time = () => time;
        line.Connection = new ReplaySerialConnection([0xFF]);
        line.Update();

        // The emulation clock does not move the line.
        _clock.Elapsed = TimeSpan.FromSeconds(1);
        line.Update();
        Assert.False(Cts());

        time = TimeSpan.FromSeconds(1.5 * BitTime);
        line.Update();
        Assert.True(Cts());

        time = TimeSpan.Zero;
        line.Update();
        Assert.True(Cts());
    }

    [Fact]
    public void DsrLine_UsesTheDsrPin()
    {
        _chipset.DsrLine.Connection = new ReplaySerialConnection([0x00]);

        _chipset.DsrLine.Update();

        Assert.Equal(0, _memory.Read8(CiaBPra) & 0x08);
        Assert.True(Cts());
    }

    private bool Cts() => (_memory.Read8(CiaBPra) & 0x10) != 0;
}
