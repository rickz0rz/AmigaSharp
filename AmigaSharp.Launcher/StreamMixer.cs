using System.Runtime.InteropServices;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Launcher;

/// <summary>A layer of the sound of the stream.</summary>
public enum MixerLayer
{
    /// <summary>The sound of the current video of the genlock playlist.</summary>
    Video,

    /// <summary>The sound of the music playlist.</summary>
    Music,

    /// <summary>The sound of the audio channels of the Amiga.</summary>
    Amiga,
}

/// <summary>The settings of a layer. A change of the volume or of the mute takes <see cref="Fade"/> seconds.</summary>
public sealed record LayerSettings(double Volume = 1, bool Muted = false, double Fade = 0.5);

/// <summary>When the music becomes quieter.</summary>
public enum DuckCondition
{
    /// <summary>While the current video of the genlock has sound.</summary>
    VideoHasSound,

    /// <summary>Never. An outside program can change the volume of the music itself.</summary>
    Never,
}

/// <summary>
/// How the music becomes quieter: when, to which part of its volume (0 stops it), and in how many seconds.
/// </summary>
public sealed record DuckSettings(DuckCondition When = DuckCondition.VideoHasSound, double Volume = 0, double Fade = 0.5);

/// <summary>
/// Mixes the sound of the stream, for one picture at a time, from three layers: the sound of the current video of the
/// genlock playlist, the music playlist, and the sound of the audio channels of the Amiga. The samples are 16-bit
/// stereo at 48 kHz.
/// </summary>
/// <remarks>
/// <para>
/// Each layer has a volume and a mute. A change goes to the new gain in the fade time of the layer, so it does not
/// click. The music also becomes quieter as the duck settings say: by default, it stops while a video with sound plays.
/// While the music is silent, its playlist does not move, so it continues from the same place.
/// </para>
/// <para>
/// The emulation makes the sound of the Amiga at its own rate, and the stream takes it at the rate of the stream. The
/// two clocks are a little different. If the buffer of the Amiga sound becomes too full, the mixer drops the oldest
/// samples. If it is empty, the mixer adds silence.
/// </para>
/// <para>
/// The mixer keeps the peak level of each layer (0 to 1, after the gain) for the last full second, so that an outside
/// program can see if a layer is silent.
/// </para>
/// </remarks>
public sealed class StreamMixer
{
    public const int SampleRate = 48_000;
    private const int AmigaTarget = SampleRate / 10;
    private const int AmigaMaximum = SampleRate * 3 / 10;
    private const int Layers = 3;

    private readonly object _lock = new();
    private readonly GenlockPlaylist? _music;
    private readonly AudioTap? _amiga;
    private readonly LayerSettings[] _settings = [new(), new(), new()];
    private readonly Ramp[] _gains = [new(1), new(1), new(1)];
    private readonly Ramp _duckGain = new(1);
    private readonly double[] _peaks = new double[Layers];
    private readonly double[] _levels = new double[Layers];
    private DuckSettings _duck = new();
    private int _levelSamples;
    private int[] _sum = [];
    private short[] _layer = [];
    private byte[] _bytes = [];

    /// <param name="music">The music playlist. The mixer takes one picture of it for each picture of the stream.</param>
    /// <param name="amiga">The sound of the Amiga, or null.</param>
    public StreamMixer(GenlockPlaylist? music, AudioTap? amiga)
    {
        _music = music;
        _amiga = amiga;
    }

    /// <summary>The settings of a layer.</summary>
    public LayerSettings Get(MixerLayer layer)
    {
        lock (_lock)
            return _settings[(int)layer];
    }

    /// <summary>Changes the settings of a layer. The gain goes to the new value in the fade time.</summary>
    public void Set(MixerLayer layer, LayerSettings settings)
    {
        lock (_lock)
        {
            _settings[(int)layer] = settings;
            _gains[(int)layer].MoveTo(settings.Muted ? 0 : settings.Volume, settings.Fade);
        }
    }

    /// <summary>When and how the music becomes quieter.</summary>
    public DuckSettings Duck
    {
        get
        {
            lock (_lock)
                return _duck;
        }
        set
        {
            lock (_lock)
                _duck = value;
        }
    }

    /// <summary>The peak level of a layer in the last full second: 0 (silent) to 1 (full scale).</summary>
    public double Level(MixerLayer layer)
    {
        lock (_lock)
            return _levels[(int)layer];
    }

    /// <summary>The gain of the music now, with the duck: 1 when it plays at its volume, 0 when it stopped.</summary>
    public double MusicGain
    {
        get
        {
            lock (_lock)
                return _gains[(int)MixerLayer.Music].Value * _duckGain.Value;
        }
    }

    /// <summary>Fills the output (left, right, ...) with the sound of one picture.</summary>
    /// <param name="item">The sound of the current video, or null.</param>
    /// <param name="itemHasSound">True if the current video has sound. By default, the music then stops.</param>
    public void Mix(Span<short> output, PcmBuffer? item, bool itemHasSound)
    {
        lock (_lock)
        {
            if (_sum.Length < output.Length)
            {
                _sum = new int[output.Length];
                _layer = new short[output.Length];
                _bytes = new byte[output.Length * 2];
            }

            var sum = _sum.AsSpan(0, output.Length);
            var layer = _layer.AsSpan(0, output.Length);
            var bytes = _bytes.AsSpan(0, output.Length * 2);
            sum.Clear();

            // The sound of the video goes with its picture, so the mixer reads it also when the layer is silent.
            if (item != null)
            {
                item.Read(bytes, TimeSpan.FromSeconds(1));
                Add(sum, MemoryMarshal.Cast<byte, short>(bytes), _gains[(int)MixerLayer.Video], null, MixerLayer.Video);
            }
            else
            {
                Add(sum, [], _gains[(int)MixerLayer.Video], null, MixerLayer.Video);
            }

            var duck = _duck.When == DuckCondition.VideoHasSound && itemHasSound;
            _duckGain.MoveTo(duck ? _duck.Volume : 1, _duck.Fade);
            var music = _gains[(int)MixerLayer.Music];
            if (_music != null && (music.Value * _duckGain.Value > 0 || music.Target * _duckGain.Target > 0))
            {
                if (_music.TakeFrame(TimeSpan.Zero) is { } frame)
                {
                    frame.Audio.Read(bytes, TimeSpan.FromMilliseconds(100));
                    Add(sum, MemoryMarshal.Cast<byte, short>(bytes), music, _duckGain, MixerLayer.Music);
                }
                else
                {
                    Add(sum, [], music, _duckGain, MixerLayer.Music);
                }
            }
            else
            {
                music.Finish();
                _duckGain.Finish();
            }

            if (_amiga != null)
            {
                if (_amiga.Available > AmigaMaximum)
                    _amiga.Skip(_amiga.Available - AmigaTarget);
                var read = _amiga.Read(layer) * 2;
                layer[read..].Clear();
                Add(sum, layer, _gains[(int)MixerLayer.Amiga], null, MixerLayer.Amiga);
            }

            for (var i = 0; i < output.Length; i++)
                output[i] = (short)Math.Clamp(sum[i], short.MinValue, short.MaxValue);

            _levelSamples += output.Length / 2;
            if (_levelSamples >= SampleRate)
            {
                _levelSamples = 0;
                _peaks.CopyTo(_levels, 0);
                Array.Clear(_peaks);
            }
        }
    }

    /// <summary>
    /// Adds the samples of a layer (none for silence) with the gain, which moves one step for each stereo sample, and
    /// keeps the peak of the layer.
    /// </summary>
    private void Add(Span<int> sum, ReadOnlySpan<short> samples, Ramp gain, Ramp? duck, MixerLayer layer)
    {
        var peak = _peaks[(int)layer];
        for (var i = 0; i < sum.Length; i += 2)
        {
            var factor = gain.Next() * (duck?.Next() ?? 1);
            if (samples.IsEmpty)
                continue;
            var left = samples[i] * factor;
            var right = samples[i + 1] * factor;
            sum[i] += (int)left;
            sum[i + 1] += (int)right;
            peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)) / 32768);
        }

        _peaks[(int)layer] = peak;
    }

    /// <summary>A gain that moves to its target by a step for each stereo sample.</summary>
    private sealed class Ramp(double value)
    {
        private double _step;

        public double Value { get; private set; } = value;
        public double Target { get; private set; } = value;

        /// <summary>Moves to the target in the fade time. A fade of 0 changes the gain at once.</summary>
        public void MoveTo(double target, double fadeSeconds)
        {
            if (target == Target && _step > 0)
                return;
            Target = target;
            _step = fadeSeconds <= 0 ? double.PositiveInfinity : Math.Abs(target - Value) / (fadeSeconds * SampleRate);
        }

        /// <summary>Moves the gain one step, and gives the gain for this sample.</summary>
        public double Next()
        {
            Value = Value < Target ? Math.Min(Target, Value + _step) : Math.Max(Target, Value - _step);
            return Value;
        }

        /// <summary>Goes to the target at once, for a layer that the mixer does not play now.</summary>
        public void Finish() => Value = Target;
    }
}
