using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Launcher;

/// <summary>The pictures and the sound of one video of the genlock playlist.</summary>
public interface IGenlockDecoder : IDisposable
{
    /// <summary>The sound of the pictures. It has silence if the source has no sound.</summary>
    PcmBuffer Audio { get; }

    /// <summary>True if the source has sound. The music of the stream then stops while the source plays.</summary>
    bool HasSound { get; }

    /// <summary>The number of pictures in the queue.</summary>
    int BufferedFrames { get; }

    /// <summary>True when the source ended or failed, and the stream took all its pictures.</summary>
    bool IsCompleted { get; }

    /// <summary>Takes the next picture, as 0xAARRGGBB pixels.</summary>
    bool TryTake(out uint[] pixels, TimeSpan timeout);
}

/// <summary>
/// Decodes one video source (a file or a URL) with ffmpeg: pictures of the size of the display at 29.97 each second,
/// and optionally the sound as PCM (48 kHz, stereo, 16 bits).
/// </summary>
/// <remarks>
/// <para>
/// The decoder deinterlaces an interlaced video, cuts it to 4:3 (with its pixel aspect ratio), and scales it to the
/// picture of the display, which shows as 4:3. It reads the source at its real rate. A file can play in a loop. A
/// source with no video, for example a music file, gives black pictures while its sound plays.
/// </para>
/// <para>
/// The pictures go to a queue, and the sound goes to <see cref="Audio"/>. The stream takes the sound of 1/29.97 second
/// for each picture that it takes, so the two stay together. When the queue is full, the decoder waits. When the source
/// ends or fails, <see cref="IsCompleted"/> becomes true after the last picture.
/// </para>
/// </remarks>
public sealed class GenlockDecoder : IGenlockDecoder
{
    public const int SampleRate = 48_000;
    public const int BytesPerSample = 4;
    private const int FrameBytes = Display.Width * Display.Height * 4;
    private const int MaximumFrames = 60;

    // The pictures of all decoders use the same arrays again.
    private static readonly ConcurrentBag<uint[]> Pool = [];

    private readonly string _source;
    private readonly bool _loop;
    private readonly bool _withAudio;
    private readonly TextWriter _log;
    private readonly BlockingCollection<uint[]> _frames = new(MaximumFrames);
    private readonly Thread _thread;
    private volatile bool _stopped;
    private Process? _process;

    /// <param name="loop">True to play a file again from its start when it ends.</param>
    /// <param name="withAudio">True to decode the sound of the source too.</param>
    public GenlockDecoder(string source, bool loop, bool withAudio, TextWriter log)
    {
        _source = source;
        _loop = loop;
        _withAudio = withAudio;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Genlock decoder" };
        _thread.Start();
    }

    /// <summary>The sound of the pictures. It has silence if the source has no sound.</summary>
    public PcmBuffer Audio { get; } = new();

    /// <summary>True if the source has sound. It is false until ffprobe found the streams of the source.</summary>
    public bool HasSound => _hasSound;

    private volatile bool _hasSound;

    /// <summary>The number of pictures in the queue.</summary>
    public int BufferedFrames => _frames.Count;

    /// <summary>True when the source ended or failed, and the stream took all its pictures.</summary>
    public bool IsCompleted => _frames.IsCompleted;

    /// <summary>Takes the next picture, as 0xAARRGGBB pixels. Give the array back with <see cref="Return"/>.</summary>
    public bool TryTake(out uint[] pixels, TimeSpan timeout)
    {
        try
        {
            return _frames.TryTake(out pixels!, timeout);
        }
        catch (ObjectDisposedException)
        {
            pixels = null!;
            return false;
        }
    }

    public static void Return(uint[] pixels) => Pool.Add(pixels);

    /// <summary>The duration of a file in seconds, from ffprobe, or null for a live source or an unknown duration.</summary>
    public static double? Duration(string source)
    {
        if (!File.Exists(source))
            return null;
        var output = Probe(["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", source]);
        return double.TryParse(output?.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
    }

    public void Dispose()
    {
        _stopped = true;
        try
        {
            _process?.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        _thread.Join(TimeSpan.FromSeconds(3));
        while (_frames.TryTake(out var pixels))
            Return(pixels);
        Audio.Complete();
    }

    private void Run()
    {
        try
        {
            // A short analysis finds the streams of a live source in about 1.5 seconds. The default takes 6. If ffprobe
            // cannot run, assume a video with sound.
            var streams = Probe(["-v", "error", "-analyzeduration", "500000", "-probesize", "500000",
                "-show_entries", "stream=codec_type", "-of", "csv=p=0", _source]) ?? "video\naudio";
            var audio = _withAudio && streams.Contains("audio");
            _hasSound = audio;
            if (!_stopped)
                Decode(audio, video: streams.Contains("video"));
        }
        finally
        {
            _frames.CompleteAdding();
            Audio.Complete();
        }
    }

    /// <summary>Runs ffprobe and returns its output, or null if it cannot run.</summary>
    private static string? Probe(string[] arguments)
    {
        var start = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using var probe = Process.Start(start)!;
            var output = probe.StandardOutput.ReadToEnd();
            probe.WaitForExit();
            return output;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private void Decode(bool audio, bool video)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string[] input = File.Exists(_source)
            ? [.. _loop ? new[] { "-stream_loop", "-1" } : [], "-readrate", "1", "-i", _source]
            // A live source starts in the middle of the video: a short analysis finds the first picture sooner, and the
            // decoder continues after the errors before the first complete picture.
            : ["-analyzeduration", "1000000", "-probesize", "1000000", "-max_error_rate", "1",
                "-readrate", "1", "-readrate_initial_burst", "1", "-readrate_catchup", "1.05", "-i", _source];
        string[] audioOutput = audio
            ? ["-map", "0:a:0", "-af", "aresample=async=1:first_pts=0", "-f", "s16le", "-ar", SampleRate.ToString(),
                "-ac", "2", $"tcp://127.0.0.1:{port}"]
            : [];
        // A source with no video gets black pictures from ffmpeg, as long as its sound for a file.
        string[] black = video
            ? []
            : ["-f", "lavfi", "-i", $"color=c=black:s={Display.Width}x{Display.Height}:r=30000/1001" +
                                   (Duration(_source) is { } seconds && !_loop
                                       ? $":d={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                                       : "")];
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            .. input,
            .. black,
            "-map", video ? "0:v:0" : "1:v:0",
            // The pictures and the sound both start at the start of the input: the first picture repeats and the sound
            // starts with silence as necessary. The sound follows the times of the input, so the two stay together.
            "-vf", "bwdif=mode=send_frame:deint=interlaced,fps=30000/1001:start_time=0," +
                   "crop=w='min(iw,ih*4/3/sar)':h='min(ih,iw*sar*3/4)'," +
                   $"scale={Display.Width}:{Display.Height},format=bgra",
            "-f", "rawvideo", "pipe:1",
            .. audioOutput,
        ];
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process == null)
            return;
        _process = process;
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data) && !_stopped && !IsStartupNoise(e.Data))
                _log.WriteLine($"genlock: {e.Data}");
        };
        process.BeginErrorReadLine();

        Thread? audioThread = null;
        if (audio)
        {
            audioThread = new Thread(() => ReceiveAudio(listener)) { IsBackground = true, Name = "Genlock audio" };
            audioThread.Start();
        }
        else
        {
            Audio.Complete();
        }

        ReadPictures(process.StandardOutput.BaseStream);
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        process.WaitForExit();
        listener.Stop();
        audioThread?.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// The messages of the decoder when a live source starts in the middle of the video, before its first complete
    /// picture. They are normal.
    /// </summary>
    private static bool IsStartupNoise(string line) =>
        line.Contains("Invalid frame dimensions") || line.Contains("Error submitting packet to decoder")
        || line.Contains("Last message repeated");

    private void ReadPictures(Stream input)
    {
        var bytes = new byte[FrameBytes];
        while (!_stopped)
        {
            var count = 0;
            while (count < FrameBytes)
            {
                var read = input.Read(bytes, count, FrameBytes - count);
                if (read == 0)
                    return;
                count += read;
            }

            var pixels = Pool.TryTake(out var reused) ? reused : new uint[Display.Width * Display.Height];
            Buffer.BlockCopy(bytes, 0, pixels, 0, FrameBytes);
            // A full queue makes the decoder wait. A picture must not go, because its sound would stay.
            while (!_stopped && !_frames.TryAdd(pixels, TimeSpan.FromMilliseconds(100)))
            {
            }
        }
    }

    private void ReceiveAudio(TcpListener listener)
    {
        try
        {
            using var client = listener.AcceptTcpClient();
            var stream = client.GetStream();
            var block = new byte[SampleRate * BytesPerSample / 50];
            int read;
            while ((read = stream.Read(block, 0, block.Length)) > 0)
                Audio.Write(block.AsSpan(0, read));
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            Audio.Complete();
        }
    }
}

/// <summary>A buffer of PCM bytes that one thread writes and another thread reads.</summary>
public sealed class PcmBuffer
{
    private readonly object _lock = new();
    private readonly Queue<byte[]> _blocks = new();
    private int _offset;
    private bool _complete;

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            _blocks.Enqueue(data.ToArray());
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>No more data comes.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            _complete = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Fills the target with the next bytes. Waits up to the timeout for bytes that did not come yet. The rest of the
    /// target is silence. Returns the number of bytes of data.
    /// </summary>
    public int Read(Span<byte> target, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var filled = 0;
        lock (_lock)
        {
            while (filled < target.Length)
            {
                if (_blocks.Count == 0)
                {
                    var remaining = deadline - Stopwatch.GetTimestamp();
                    if (_complete || remaining <= 0)
                        break;
                    Monitor.Wait(_lock, TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
                    continue;
                }

                var block = _blocks.Peek();
                var count = Math.Min(block.Length - _offset, target.Length - filled);
                block.AsSpan(_offset, count).CopyTo(target[filled..]);
                filled += count;
                _offset += count;
                if (_offset == block.Length)
                {
                    _blocks.Dequeue();
                    _offset = 0;
                }
            }
        }

        target[filled..].Clear();
        return filled;
    }
}
