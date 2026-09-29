using System.Runtime.InteropServices;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Launcher;

/// <summary>
/// Mixes the sound of the stream, for one picture at a time: the sound of the current video of the genlock playlist,
/// the music of the audio playlist, and the sound of the audio channels of the Amiga. The samples are 16-bit stereo at
/// 48 kHz.
/// </summary>
/// <remarks>
/// <para>
/// The music plays when the current video has no sound, for example when no video plays. While a video with sound
/// plays, the music fades out in half a second and stops. It continues from the same place when it fades in again.
/// </para>
/// <para>
/// The emulation makes the sound of the Amiga at its own rate, and the stream takes it at the rate of the stream. The
/// two clocks are a little different. If the buffer of the Amiga sound becomes too full, the mixer drops the oldest
/// samples. If it is empty, the mixer adds silence.
/// </para>
/// </remarks>
public sealed class StreamMixer(Action<Span<short>>? music, AudioTap? amiga)
{
    public const int SampleRate = 48_000;
    private const double FadeSeconds = 0.5;
    private const int AmigaTarget = SampleRate / 10;
    private const int AmigaMaximum = SampleRate * 3 / 10;

    private int[] _sum = [];
    private short[] _layer = [];
    private byte[] _bytes = [];
    private double _musicGain = 1;

    /// <summary>The gain of the music now: 1 when it plays, 0 when it stopped under the sound of a video.</summary>
    public double MusicGain => _musicGain;

    /// <summary>Fills the output (left, right, ...) with the sound of one picture.</summary>
    /// <param name="item">The sound of the current video, or null.</param>
    /// <param name="itemHasSound">True if the current video has sound. The music then fades out.</param>
    public void Mix(Span<short> output, PcmBuffer? item, bool itemHasSound)
    {
        if (_sum.Length < output.Length)
        {
            _sum = new int[output.Length];
            _layer = new short[output.Length];
            _bytes = new byte[output.Length * 2];
        }

        var sum = _sum.AsSpan(0, output.Length);
        var layer = _layer.AsSpan(0, output.Length);
        sum.Clear();

        if (item != null)
        {
            var bytes = _bytes.AsSpan(0, output.Length * 2);
            item.Read(bytes, TimeSpan.FromSeconds(1));
            Add(sum, MemoryMarshal.Cast<byte, short>(bytes));
        }

        var target = itemHasSound ? 0.0 : 1.0;
        if (music != null && (_musicGain > 0 || target > 0))
        {
            music(layer);
            var step = 1 / (FadeSeconds * SampleRate);
            for (var i = 0; i < layer.Length; i += 2)
            {
                _musicGain = _musicGain < target ? Math.Min(target, _musicGain + step) : Math.Max(target, _musicGain - step);
                sum[i] += (int)(layer[i] * _musicGain);
                sum[i + 1] += (int)(layer[i + 1] * _musicGain);
            }
        }
        else
        {
            _musicGain = target;
        }

        if (amiga != null)
        {
            if (amiga.Available > AmigaMaximum)
                amiga.Skip(amiga.Available - AmigaTarget);
            var read = amiga.Read(layer) * 2;
            layer[read..].Clear();
            Add(sum, layer);
        }

        for (var i = 0; i < output.Length; i++)
            output[i] = (short)Math.Clamp(sum[i], short.MinValue, short.MaxValue);
    }

    private static void Add(Span<int> sum, ReadOnlySpan<short> samples)
    {
        for (var i = 0; i < sum.Length; i++)
            sum[i] += samples[i];
    }
}
