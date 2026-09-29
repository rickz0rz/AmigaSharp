using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

public class AudioOutputTests
{
    private const uint Buffer = 0x1000;
    private const uint NextBuffer = 0x2000;
    private const int Period = 1000;

    // An output sample is 3579545 / 48000 = 74.6 color clocks. An Amiga sample of 1000 color clocks is 13.4 of them.
    private const double ClocksPerOutputSample = Beam.ColorClockHz / AudioOutput.SampleRate;

    private readonly Memory _memory = new();
    private readonly Chipset _chipset;

    public AudioOutputTests()
    {
        _chipset = new Chipset(new ManualClock(), _memory);
        _memory.Hardware = _chipset;
        // The power LED is dim, so the low-pass filter is off.
        _memory.Write8(0xBFE201, 0x02);
        _memory.Write8(0xBFE001, 0x02);
        _memory.Write16(Buffer, 0x40C0); // 64, then -64.
        _memory.Write16(NextBuffer, 0x2020); // 32, then 32.
    }

    [Fact]
    public void Channel0_PlaysEachSampleForItsPeriod_OnTheLeft_AndPlaysTheBufferAgain()
    {
        StartChannel(0, volume: 64);

        var samples = Play(40);

        // 64 * volume 64, times 2 to fill the 16-bit range with two channels.
        Assert.Equal((8192, 0), samples[0]);
        Assert.Equal((-8192, 0), samples[At(1500)]);
        Assert.Equal((8192, 0), samples[At(2500)]);
    }

    [Fact]
    public void Channel1_PlaysOnTheRight_AtItsVolume()
    {
        StartChannel(1, volume: 32);

        var samples = Play(5);

        Assert.Equal((0, 4096), samples[0]);
    }

    [Fact]
    public void EndOfTheBuffer_LoadsTheLocationThatTheProgramWroteMeanwhile()
    {
        StartChannel(0, volume: 64);
        Play(1);
        _memory.Write32(0xDFF0A0, NextBuffer);

        var samples = Play(40);

        // The first two samples were read before. So index i is output sample i + 2.
        Assert.Equal((-8192, 0), samples[At(1500) - 2]);
        Assert.Equal((4096, 0), samples[At(2500) - 2]);
    }

    [Fact]
    public void LowPassFilter_SmoothsTheStartOfASample()
    {
        _memory.Write8(0xBFE001, 0x00);
        StartChannel(0, volume: 64);

        var samples = Play(5);

        Assert.InRange(samples[0].Left, 1, 8191);
        Assert.True(samples[4].Left > samples[0].Left);
    }

    private void StartChannel(int channel, int volume)
    {
        var registers = 0xDFF0A0 + (uint)channel * 0x10;
        _memory.Write32(registers, Buffer);
        _memory.Write16(registers + 4, 1);
        _memory.Write16(registers + 6, Period);
        _memory.Write16(registers + 8, (ushort)volume);
        _memory.Write16(0xDFF096, (ushort)(0x8200 | 1 << channel));
        _chipset.Audio.Update(0);
        _clock = 0;
    }

    private double _clock;

    /// <summary>Plays the number of output samples, and returns them.</summary>
    private List<(int Left, int Right)> Play(int count)
    {
        _clock += count * ClocksPerOutputSample;
        _chipset.Audio.Update((long)_clock);
        var buffer = new short[_chipset.Audio.Available * 2];
        var read = _chipset.Audio.Read(buffer);
        return Enumerable.Range(0, read).Select(i => ((int)buffer[i * 2], (int)buffer[i * 2 + 1])).ToList();
    }

    private static int At(double colorClock) => (int)(colorClock / ClocksPerOutputSample);
}
