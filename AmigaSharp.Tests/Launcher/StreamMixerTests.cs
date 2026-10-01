using System.Runtime.InteropServices;
using AmigaSharp.Host;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Launcher;

public class StreamMixerTests
{
    // The sound of one picture of the stream: 1601 stereo samples.
    private const int Samples = 1601;

    // A fade of half a second is 15 pictures.
    private const int FadePictures = 16;

    private FakeDecoder? _musicDecoder;

    [Fact]
    public void ItemSound_GoesThrough_AndTheMusicStopsUnderIt()
    {
        var mixer = new StreamMixer(Music(1000), amiga: null);
        var item = Item(value: 3000, pictures: 40);

        for (var picture = 0; picture < FadePictures; picture++)
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

        for (var picture = 0; picture < FadePictures; picture++)
            Mix(mixer, Item(0, 1), hasSound: true);
        var takenWhileOut = _musicDecoder!.Taken;
        for (var picture = 0; picture < 20; picture++)
            Mix(mixer, Item(0, 1), hasSound: true);

        Assert.Equal(takenWhileOut, _musicDecoder.Taken);
        var output = Mix(mixer, item: null, hasSound: false);
        // It fades in again from 0.
        Assert.InRange(output[0], 0, 100);
        Assert.True(output[^1] > output[0]);
    }

    [Fact]
    public void Layers_AreAdded_AndTheSumIsClipped()
    {
        var mixer = new StreamMixer(Music(30000), amiga: null);

        var output = Mix(mixer, Item(10000, 1), hasSound: false);

        Assert.All(output, sample => Assert.Equal(short.MaxValue, sample));
    }

    [Fact]
    public void Volume_GoesToItsNewValue_InItsFadeTime()
    {
        var mixer = new StreamMixer(music: null, amiga: null);
        mixer.Set(MixerLayer.Video, new LayerSettings(Volume: 0.5, Fade: 0));
        Assert.All(Mix(mixer, Item(4000, 1), hasSound: true), sample => Assert.Equal(2000, sample));

        mixer.Set(MixerLayer.Video, new LayerSettings(Volume: 0.25, Fade: 0.5));
        var first = Mix(mixer, Item(4000, 1), hasSound: true);
        Assert.InRange(first[^1], 1001, 1999);
        for (var picture = 0; picture < FadePictures; picture++)
            Mix(mixer, Item(4000, 1), hasSound: true);
        Assert.All(Mix(mixer, Item(4000, 1), hasSound: true), sample => Assert.Equal(1000, sample));
    }

    [Fact]
    public void MutedVideo_IsSilent_ButItsSoundIsStillRead()
    {
        var mixer = new StreamMixer(music: null, amiga: null);
        mixer.Set(MixerLayer.Video, new LayerSettings(Muted: true, Fade: 0));
        var item = Item(4000, 2);

        Assert.All(Mix(mixer, item, hasSound: true), sample => Assert.Equal(0, sample));

        mixer.Set(MixerLayer.Video, new LayerSettings(Fade: 0));
        Assert.All(Mix(mixer, item, hasSound: true), sample => Assert.Equal(4000, sample));
        // The sound of two pictures was in the buffer: the muted picture took the first.
        Assert.All(Mix(mixer, item, hasSound: true), sample => Assert.Equal(0, sample));
    }

    [Theory]
    [InlineData("video-has-sound", 0.25, 250)]
    [InlineData("never", 0.25, 1000)]
    public void Duck_KeepsItsPartOfTheMusic_OrDoesNothing(string when, double volume, int expected)
    {
        var mixer = new StreamMixer(Music(1000), amiga: null)
        {
            Duck = new DuckSettings(when == "never" ? DuckCondition.Never : DuckCondition.VideoHasSound, volume, 0.5),
        };

        for (var picture = 0; picture < FadePictures; picture++)
            Mix(mixer, Item(0, 1), hasSound: true);

        Assert.All(Mix(mixer, Item(0, 1), hasSound: true), sample => Assert.Equal(expected, sample));
    }

    [Fact]
    public void Level_IsThePeakOfTheLastSecond()
    {
        var mixer = new StreamMixer(Music(16384), amiga: null);
        Assert.Equal(0, mixer.Level(MixerLayer.Music));

        for (var picture = 0; picture < 30; picture++)
            Mix(mixer, item: null, hasSound: false);

        Assert.Equal(0.5, mixer.Level(MixerLayer.Music), 3);
        Assert.Equal(0, mixer.Level(MixerLayer.Video));
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
        chipset.Audio.Update((long)VideoStandard.Ntsc.ColorClockHz);
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

    /// <summary>A music playlist with one item: a constant sound for 1000 pictures.</summary>
    private GenlockPlaylist Music(short value)
    {
        var playlist = new GenlockPlaylist(withAudio: true, TextWriter.Null, _ =>
        {
            _musicDecoder = new FakeDecoder(Item(value, 1000));
            return _musicDecoder;
        }, name: "Music");
        playlist.Add("http://music.local/stream", seconds: null, loop: false, next: false);
        return playlist;
    }

    private static PcmBuffer Item(short value, int pictures)
    {
        var buffer = new PcmBuffer();
        var data = new short[Samples * pictures * 2];
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

    /// <summary>A decoder with many pictures ready, and a sound.</summary>
    private sealed class FakeDecoder(PcmBuffer audio) : IGenlockDecoder
    {
        public PcmBuffer Audio => audio;
        public bool HasSound => true;
        public int BufferedFrames => 1000 - Taken;
        public bool IsCompleted => false;
        public int Taken { get; private set; }

        public bool TryTake(out uint[] pixels, TimeSpan timeout)
        {
            pixels = new uint[4];
            Taken++;
            return true;
        }

        public void Dispose()
        {
        }
    }
}
