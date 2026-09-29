using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Launcher;

/// <summary>
/// Decodes the video of a genlock source (a file or a URL) with ffmpeg: pictures of the size of the display at 29.97
/// each second, and optionally the sound as PCM (48 kHz, stereo, 16 bits).
/// </summary>
/// <remarks>
/// <para>
/// The decoder deinterlaces an interlaced video, cuts it to 4:3 (with its pixel aspect ratio), and scales it to the
/// picture of the display, which shows as 4:3. It reads the source at its real rate. A file plays in a loop.
/// </para>
/// <para>
/// Each start of the decoder is a session. When a live source stops, the decoder ends, and a new session starts after
/// 2 seconds. The pictures of a session go to a queue, and the sound of a session goes to its own buffer. The stream
/// takes the sound of a session for each picture that it takes, so the two stay together.
/// </para>
/// </remarks>
public sealed class GenlockSource : IDisposable
{
    public const int SampleRate = 48_000;
    public const int BytesPerSample = 4;
    private const int FrameBytes = Display.Width * Display.Height * 4;
    private const int MaximumFrames = 60;
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private readonly string _source;
    private readonly bool _withAudio;
    private readonly TextWriter _log;
    private readonly BlockingCollection<(uint[] Pixels, Session Session)> _frames = new(MaximumFrames);
    private readonly ConcurrentBag<uint[]> _pool = [];
    private readonly Thread _thread;
    private volatile bool _stopped;
    private Process? _decoder;

    /// <param name="withAudio">True to decode the sound of the source too.</param>
    public GenlockSource(string source, bool withAudio, TextWriter log)
    {
        _source = source;
        _withAudio = withAudio;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Genlock source" };
        _thread.Start();
    }

    /// <summary>The number of pictures in the queue.</summary>
    public int BufferedFrames => _frames.Count;

    /// <summary>Takes the next picture, as 0xAARRGGBB pixels. Give the array back with <see cref="Return"/>.</summary>
    public bool TryTake(out uint[] pixels, out Session session, TimeSpan timeout)
    {
        if (_frames.TryTake(out var frame, timeout))
        {
            (pixels, session) = frame;
            return true;
        }

        (pixels, session) = (null!, null!);
        return false;
    }

    public void Return(uint[] pixels) => _pool.Add(pixels);

    public void Dispose()
    {
        _stopped = true;
        try
        {
            _decoder?.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        _thread.Join(TimeSpan.FromSeconds(3));
    }

    private void Run()
    {
        while (!_stopped)
        {
            var audio = _withAudio && HasAudio();
            RunSession(audio);
            if (_stopped)
                return;
            _log.WriteLine($"The genlock source stopped. It starts again in {RestartDelay.TotalSeconds:F0} seconds.");
            Thread.Sleep(RestartDelay);
        }
    }

    /// <summary>True if the source has sound. ffprobe reads the start of the source.</summary>
    private bool HasAudio()
    {
        var start = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_type", "-of", "csv=p=0",
                     _source,
                 })
            start.ArgumentList.Add(argument);
        try
        {
            using var probe = Process.Start(start)!;
            var output = probe.StandardOutput.ReadToEnd();
            probe.WaitForExit();
            return output.Contains("audio");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return true;
        }
    }

    private void RunSession(bool audio)
    {
        var session = new Session();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var file = File.Exists(_source);
        string[] input = file
            ? ["-stream_loop", "-1", "-readrate", "1", "-i", _source]
            : ["-readrate", "1", "-readrate_initial_burst", "1", "-readrate_catchup", "1.05", "-i", _source];
        string[] audioOutput = audio
            ? ["-map", "0:a:0", "-af", "aresample=async=1:first_pts=0", "-f", "s16le", "-ar", SampleRate.ToString(),
                "-ac", "2", $"tcp://127.0.0.1:{port}"]
            : [];
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            .. input,
            "-map", "0:v:0",
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

        using var decoder = Process.Start(start);
        if (decoder == null)
            return;
        _decoder = decoder;
        decoder.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _log.WriteLine($"genlock: {e.Data}");
        };
        decoder.BeginErrorReadLine();

        Thread? audioThread = null;
        if (audio)
        {
            audioThread = new Thread(() => ReceiveAudio(listener, session)) { IsBackground = true, Name = "Genlock audio" };
            audioThread.Start();
        }
        else
        {
            session.Audio.Complete();
        }

        ReadPictures(decoder.StandardOutput.BaseStream, session);
        if (!decoder.HasExited)
            decoder.Kill(entireProcessTree: true);
        decoder.WaitForExit();
        _decoder = null;
        listener.Stop();
        audioThread?.Join(TimeSpan.FromSeconds(2));
        session.Audio.Complete();
    }

    private void ReadPictures(Stream input, Session session)
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

            var pixels = _pool.TryTake(out var reused) ? reused : new uint[Display.Width * Display.Height];
            Buffer.BlockCopy(bytes, 0, pixels, 0, FrameBytes);
            // A full queue makes the decoder wait. A picture must not go, because its sound would stay.
            while (!_stopped && !_frames.TryAdd((pixels, session), TimeSpan.FromMilliseconds(100)))
            {
            }
        }
    }

    private static void ReceiveAudio(TcpListener listener, Session session)
    {
        try
        {
            using var client = listener.AcceptTcpClient();
            var stream = client.GetStream();
            var block = new byte[SampleRate * BytesPerSample / 50];
            int read;
            while ((read = stream.Read(block, 0, block.Length)) > 0)
                session.Audio.Write(block.AsSpan(0, read));
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            session.Audio.Complete();
        }
    }

    /// <summary>One start of the decoder, with the sound of its pictures.</summary>
    public sealed class Session
    {
        public PcmBuffer Audio { get; } = new();
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
