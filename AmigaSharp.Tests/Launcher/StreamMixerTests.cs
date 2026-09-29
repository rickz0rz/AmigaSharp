using System.Runtime.InteropServices;
using AmigaSharp.Launcher;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Launcher;

public class StreamMixerTests
{
    // The sound of one picture of the stream: 1601 stereo samples.
    private const int Samples = 1601;

    private long _musicRead;

    [Fact]
    public void ItemSound_GoesThrough_AndTheMusicStopsUnderIt()
    {
        var mixer = new StreamMixer(Music(1000), amiga: null);
        var item = Item(value: 3000, samples: Samples * 40);

        // The music fades out in half a second: 15 pictures.
        for (var picture = 0; picture < 16; picture++)
            Mix(mixer, item, hasSound: true);
        var output = Mix(mixer, item, hasSound: true);

        Assert.All(output, sample => Assert.Equal(3000, sample));
        Assert.Equal(0, mixer.MusicGain);
    }

    [Fact]
    public void Music_Pauses_WhileItIsOut_AndContinuesFromThere()
    {
        var mixer = new StreamMixer(Music(1000), amiga: null);
        Mix(mixer, item: null, hasSound: false);
        Assert.Equal(1, mixer.MusicGain);

        for (var picture = 0; picture < 20; picture++)
            Mix(mixer, Item(0, Samples), hasSound: true);
        var readWhileOut = _musicRead;
        for (var picture = 0; picture < 20; picture++)
            Mix(mixer, Item(0, Samples), hasSound: true);

        Assert.Equal(readWhileOut, _musicRead);
        var output = Mix(mixer, item: null, hasSound: false);
        // It fades in again from 0.
        Assert.InRange(output[0], 0, 100);
        Assert.True(output[^1] > output[0]);
    }

    [Fact]
    public void Layers_AreAdded_AndTheSumIsClipped()
    {
        var mixer = new StreamMixer(Music(30000), amiga: null);

        var output = Mix(mixer, Item(10000, Samples), hasSound: false);

        Assert.All(output, sample => Assert.Equal(short.MaxValue, sample));
    }

    [Fact]
    public void AmigaSound_IsMixed_AndATapThatIsTooFull_DropsItsOldestSamples()
    {
        var memory = new Memory();
        var chipset = new Chipset(new ManualClock(), memory);
        memory.Hardware = chipset;
        var tap = chipset.Audio.OpenTap();
        memory.Write8(0xBFE201, 0x02); // The power LED is dim, so the filter is off.
        memory.Write8(0xBFE001, 0x02);
        memory.Write16(0x1000, 0x4040);
        memory.Write32(0xDFF0A0, 0x1000);
        memory.Write16(0xDFF0A4, 1);
        memory.Write16(0xDFF0A6, 1000);
        memory.Write16(0xDFF0A8, 64);
        memory.Write16(0xDFF096, 0x8201);
        chipset.Audio.Update(0);
        // One second of sound, much more than the mixer keeps.
        chipset.Audio.Update((long)Beam.ColorClockHz);
        var mixer = new StreamMixer(music: null, tap);

        var output = Mix(mixer, item: null, hasSound: false);

        // 64 times the volume 64, times 2 for the range of two channels, on the left.
        Assert.Equal(8192, output[0]);
        Assert.Equal(0, output[1]);
        Assert.InRange(tap.Available, StreamMixer.SampleRate / 10 - Samples, StreamMixer.SampleRate / 10);
        // The default reader of the sound has all the samples still.
        Assert.True(chipset.Audio.Available > StreamMixer.SampleRate / 2);
    }

    [Fact]
    public void WithoutGenlock_EachPixelKeepsItsColor()
    {
        uint[] amiga = [0x0012_3456, 0xFF65_4321];
        uint[] video = [0xFFFF_FFFF, 0xFFFF_FFFF];
        var output = new uint[2];

        VideoStream.Composite(amiga, video, output, genlock: false);
        Assert.Equal([0xFF12_3456u, 0xFF65_4321u], output);

        VideoStream.Composite(amiga, video, output, genlock: true);
        Assert.Equal([0xFFFF_FFFFu, 0xFF65_4321u], output);
    }

    private Action<Span<short>> Music(short value) => target =>
    {
        target.Fill(value);
        _musicRead += target.Length;
    };

    private static PcmBuffer Item(short value, int samples)
    {
        var buffer = new PcmBuffer();
        var data = new short[samples * 2];
        Array.Fill(data, value);
        buffer.Write(MemoryMarshal.AsBytes(data.AsSpan()));
        return buffer;
    }

    private static short[] Mix(StreamMixer mixer, PcmBuffer? item, bool hasSound)
    {
        var output = new short[Samples * 2];
        mixer.Mix(output, item, hasSound);
        return output;
    }
}
