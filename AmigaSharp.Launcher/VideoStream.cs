using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Launcher;

/// <summary>
/// Streams the display as a live HLS video over HTTP, for example as a custom channel of Channels DVR. ffmpeg encodes
/// the pictures to H.264 and the sound to AAC, and writes segments of 2 seconds. The sound is a playlist of audio
/// files in a loop, or silence. An HTTP server gives the
/// segments, the playlist of the stream (/stream.m3u8) and an M3U playlist of one channel (/channels.m3u).
/// </summary>
/// <remarks>
/// <para>
/// The stream has 29.97 pictures each second, as NTSC video. A thread takes the last complete picture of the display
/// at that rate, so the emulation never waits for the encoder. The picture is 4:3: 960 by 720 pixels, or in a 1280 by
/// 720 picture with black bars at the sides for a 16:9 screen.
/// </para>
/// <para>
/// With a genlock source, the stream works as the genlock of the Prevue machine: the video of the source shows where
/// the display has the genlock key (the pixels with alpha 0, see <see cref="Display"/>). <see cref="GenlockPlaylist"/>
/// gives the video: a queue of files and URLs that the HTTP server can change (see <see cref="AnswerGenlock"/>). The
/// sender puts the video under the display, one picture of the video for each picture of the stream (see
/// <see cref="SendGenlockFrames"/>). With no video in the queue, the display shows over black. The video fills the 4:3
/// picture, and its sides are cut. A file plays in a loop at its real speed. A URL, for example a channel of Channels
/// DVR, plays live. Without an audio playlist, the stream has the sound of the source.
/// </para>
/// </remarks>
public sealed class VideoStream : IDisposable
{
    private const string PlaylistName = "stream.m3u8";
    private const double FramesPerSecond = 30000 / 1001.0;

    private readonly Display _display;
    private readonly TextWriter _log;
    private readonly string _directory = Directory.CreateTempSubdirectory("AmigaSharp-stream-").FullName;
    private readonly Process _ffmpeg;
    private readonly HttpListener _http = new();
    private readonly string _channelName;
    private readonly Thread _sender;
    private readonly AudioFeed? _audio;
    private readonly GenlockPlaylist? _genlock;

    // With a genlock source and no audio playlist, the sound of the source goes to the encoder through this port. The
    // sender adds the sound of each picture to the plan: the buffer of its session, or null for silence.
    private readonly TcpListener? _genlockAudio;
    private readonly BlockingCollection<PcmBuffer?> _audioPlan = new();
    private volatile bool _stopped;


    /// <param name="port">The TCP port of the HTTP server. It listens on all the addresses of the host.</param>
    /// <param name="wide">True to add black bars for a 16:9 picture.</param>
    /// <param name="audioPlaylist">
    /// A playlist or a directory of audio files that plays in a loop, or null for a silent stream. See
    /// <see cref="AudioFeed"/>.
    /// </param>
    /// <param name="genlockSource">
    /// The first video of the genlock playlist: a file (in a loop) or a URL, without a time limit. Null for none.
    /// </param>
    /// <param name="genlock">True for the genlock playlist, also without a first video.</param>
    public VideoStream(Display display, int port, bool wide, string channelName, TextWriter log,
        string? audioPlaylist = null, string? genlockSource = null, bool genlock = false)
    {
        _display = display;
        _log = log;
        _channelName = channelName;
        _audio = audioPlaylist == null ? null : new AudioFeed(audioPlaylist, log);
        if (genlock || genlockSource != null)
        {
            _genlock = new GenlockPlaylist(withAudio: _audio == null, log);
            if (genlockSource != null)
                _genlock.Add(genlockSource, seconds: null, loop: File.Exists(genlockSource), next: false);
            if (_audio == null)
            {
                _genlockAudio = new TcpListener(IPAddress.Loopback, 0);
                _genlockAudio.Start();
                new Thread(SendGenlockAudio) { IsBackground = true, Name = "Genlock audio relay" }.Start();
            }
        }

        var audioPort = _audio?.Port ?? (_genlockAudio?.LocalEndpoint as IPEndPoint)?.Port;
        string[] audioInput = audioPort == null
            ? ["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]
            : ["-thread_queue_size", "1024", "-f", "s16le", "-ar", AudioFeed.SampleRate.ToString(), "-ac", "2",
                "-i", $"tcp://127.0.0.1:{audioPort}"];

        var pad = wide ? ",pad=1280:720:160:0" : "";
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error",
            "-thread_queue_size", "64",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{Display.Width}x{Display.Height}", "-framerate", "30000/1001",
            "-i", "pipe:0",
            .. audioInput,
            "-map", "0:v", "-map", "1:a",
            "-vf", $"scale=960:720:flags=lanczos{pad},setsar=1",
            // The silent audio never ends. The stream ends with the pictures of the display, so that ffmpeg stops when
            // the launcher stops, also when the launcher is killed.
            "-shortest",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
            "-g", "60", "-b:v", "4M", "-maxrate", "4M", "-bufsize", "8M",
            "-c:a", "aac", "-b:a", "128k",
            "-f", "hls", "-hls_time", "2", "-hls_list_size", "10",
            "-hls_flags", "delete_segments+independent_segments",
            Path.Combine(_directory, PlaylistName),
        ];
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        _ffmpeg = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");

        _http.Prefixes.Add($"http://*:{port}/");
        _http.Start();
        _sender = new Thread(SendFrames) { IsBackground = true, Name = "Video stream" };
        _sender.Start();
        new Thread(Serve) { IsBackground = true, Name = "Video stream HTTP" }.Start();
        log.WriteLine($"Streaming on http://localhost:{port}/{PlaylistName}. The playlist of the channel is " +
                      $"http://<address of this host>:{port}/channels.m3u.");
    }

    public void Dispose()
    {
        _stopped = true;
        _http.Close();
        // The sender must stop before the pipe closes. Otherwise it writes to a closed pipe. If ffmpeg does not read,
        // the sender cannot stop, and a close of the pipe waits for its write. So stop ffmpeg first: the write then
        // fails, and the sender stops.
        if (!_sender.Join(TimeSpan.FromSeconds(2)))
        {
            _log.WriteLine("ffmpeg does not read the pictures. The launcher stops it.");
            try
            {
                _ffmpeg.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            _sender.Join(TimeSpan.FromSeconds(2));
        }

        // Close the two inputs of ffmpeg, so that it ends the stream and stops.
        _audio?.Dispose();
        _genlock?.Dispose();
        _audioPlan.CompleteAdding();
        _genlockAudio?.Stop();
        try
        {
            _ffmpeg.StandardInput.Close();
            if (!_ffmpeg.WaitForExit(5000))
                _ffmpeg.Kill();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
        }

        _ffmpeg.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Sends the last picture of the display to ffmpeg, at the frame rate of the stream.</summary>
    private void SendFrames()
    {
        if (_genlock != null)
        {
            SendGenlockFrames(_genlock);
            return;
        }

        var pixels = new uint[Display.Width * Display.Height];
        var bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        var output = _ffmpeg.StandardInput.BaseStream;
        var clock = Stopwatch.StartNew();
        for (long frame = 0; !_stopped; frame++)
        {
            var due = TimeSpan.FromSeconds(frame / FramesPerSecond);
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero)
                Thread.Sleep(wait);

            _display.CopyFrame(pixels);
            MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(bytes);
            try
            {
                output.Write(bytes);
                output.Flush();
            }
            catch (IOException)
            {
                _log.WriteLine("The video stream stopped: ffmpeg does not read the pictures.");
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sends the pictures of the display over the pictures of the genlock playlist. While a video plays, the clock of
    /// the stream follows it: the time to the next picture is a little shorter when its decoder has more pictures than
    /// the target, and a little longer when it has fewer. So the stream takes each picture of the video once, and the
    /// picture of the display at an even rate. With no picture of a video, the display shows over black.
    /// </summary>
    private void SendGenlockFrames(GenlockPlaylist playlist)
    {
        var amiga = new uint[Display.Width * Display.Height];
        var output = new uint[Display.Width * Display.Height];
        var bytes = new byte[output.Length * 4];
        var stream = _ffmpeg.StandardInput.BaseStream;
        var clock = Stopwatch.StartNew();
        var next = 0.0;
        while (!_stopped)
        {
            var wait = next - clock.Elapsed.TotalSeconds;
            if (wait > 0)
                Thread.Sleep(TimeSpan.FromSeconds(wait));

            // A picture that is late by a little does not show black, but the stream does not wait long for it.
            var frame = playlist.TakeFrame(TimeSpan.FromMilliseconds(200));
            _display.CopyFrame(amiga);
            Composite(amiga, frame?.Pixels, output);
            if (frame is { } taken)
                GenlockDecoder.Return(taken.Pixels);
            if (_genlockAudio != null)
                _audioPlan.Add(frame?.Audio);

            Buffer.BlockCopy(output, 0, bytes, 0, bytes.Length);
            try
            {
                stream.Write(bytes);
                stream.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                if (e is IOException)
                    _log.WriteLine("The video stream stopped: ffmpeg does not read the pictures.");
                return;
            }

            var correction = playlist.IsPlaying
                ? Math.Clamp(0.002 * (playlist.BufferedFrames - GenlockPlaylist.TargetFrames), -0.02, 0.02)
                : 0;
            next += 1 / FramesPerSecond * (1 - correction);
            // After a pause, do not send the missed pictures in a burst.
            if (clock.Elapsed.TotalSeconds - next > 1)
                next = clock.Elapsed.TotalSeconds;
        }
    }

    /// <summary>
    /// Puts the video under the display: where a pixel of the display has alpha 0 (the genlock key), the video shows.
    /// A pixel with an alpha between 0 and $FF, from the blend of two fields, mixes the two.
    /// </summary>
    private static void Composite(uint[] amiga, uint[]? video, uint[] output)
    {
        for (var i = 0; i < output.Length; i++)
        {
            var pixel = amiga[i];
            var alpha = pixel >> 24;
            var under = video?[i] ?? 0;
            if (alpha == 0xFF)
                output[i] = pixel;
            else if (alpha == 0)
                output[i] = 0xFF00_0000 | under;
            else
                output[i] = 0xFF00_0000 | Mix(pixel, under, alpha, 16) | Mix(pixel, under, alpha, 8) | Mix(pixel, under, alpha, 0);
        }
    }

    private static uint Mix(uint top, uint under, uint alpha, int shift) =>
        (((top >> shift & 0xFF) * alpha + (under >> shift & 0xFF) * (255 - alpha)) / 255) << shift;

    /// <summary>
    /// Sends the sound of the genlock source to the encoder: for each picture of the stream, the sound of 1/29.97
    /// second from the session of the picture, or silence.
    /// </summary>
    private void SendGenlockAudio()
    {
        try
        {
            using var client = _genlockAudio!.AcceptTcpClient();
            var output = client.GetStream();
            var buffer = new byte[GenlockDecoder.SampleRate / 20 * GenlockDecoder.BytesPerSample];
            long frame = 0;
            foreach (var audio in _audioPlan.GetConsumingEnumerable())
            {
                var samples = (long)((frame + 1) * GenlockDecoder.SampleRate / FramesPerSecond)
                              - (long)(frame * GenlockDecoder.SampleRate / FramesPerSecond);
                frame++;
                var span = buffer.AsSpan(0, (int)samples * GenlockDecoder.BytesPerSample);
                if (audio == null)
                    span.Clear();
                else
                    audio.Read(span, TimeSpan.FromSeconds(1));
                output.Write(span);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The encoder stopped.
        }
    }

    private void Serve()
    {
        while (!_stopped)
        {
            HttpListenerContext context;
            try
            {
                context = _http.GetContext();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ => Answer(context));
        }
    }

    private void Answer(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var name = context.Request.Url?.AbsolutePath.TrimStart('/') ?? "";
            if (_genlock != null && (name == "genlock" || name.StartsWith("genlock/")))
            {
                AnswerGenlock(context, _genlock, name);
                return;
            }

            if (name == "channels.m3u")
            {
                var host = context.Request.Headers["Host"] ?? $"localhost:{context.Request.LocalEndPoint.Port}";
                var playlist = "#EXTM3U\n" +
                               $"#EXTINF:-1 channel-id=\"amigasharp\" tvg-name=\"{_channelName}\",{_channelName}\n" +
                               $"http://{host}/{PlaylistName}\n";
                Send(response, "audio/x-mpegurl", Encoding.UTF8.GetBytes(playlist));
                return;
            }

            // Only the files of the stream: a name without a directory.
            var path = Path.Combine(_directory, Path.GetFileName(name));
            if (name.Length == 0 || name.Contains('/') || !File.Exists(path))
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            var type = name.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t";
            response.Headers["Cache-Control"] = name.EndsWith(".m3u8") ? "no-cache" : "max-age=60";
            Send(response, type, File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException)
        {
            // The client closed the connection, or ffmpeg deleted the segment.
            try
            {
                response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Answers a request that controls the genlock playlist. Each answer that succeeds has the playlist as JSON.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>GET /genlock: the current video and the queue.</item>
    /// <item>
    /// POST /genlock/queue: adds a video, or an array of videos, as JSON: {"source": "file or URL", "seconds": 300,
    /// "loop": false, "next": false}. Only "source" is necessary. "next" puts the video first in the queue.
    /// </item>
    /// <item>POST /genlock/next: ends the current video. The next video starts, or black shows.</item>
    /// <item>DELETE /genlock/queue: removes all videos from the queue. The current video continues.</item>
    /// <item>DELETE /genlock/queue/{id}: removes one video from the queue.</item>
    /// <item>POST /genlock/stop: removes all videos from the queue and ends the current video.</item>
    /// </list>
    /// </remarks>
    private static void AnswerGenlock(HttpListenerContext context, GenlockPlaylist playlist, string name)
    {
        var method = context.Request.HttpMethod;
        var response = context.Response;
        try
        {
            switch (method, name.TrimEnd('/'))
            {
                case ("GET", "genlock" or "genlock/queue"):
                    break;
                case ("POST", "genlock/queue"):
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    using var document = JsonDocument.Parse(reader.ReadToEnd());
                    var items = document.RootElement.ValueKind == JsonValueKind.Array
                        ? document.RootElement.EnumerateArray().ToList()
                        : [document.RootElement];
                    // Check all the videos first, so that a request with an error adds none of them.
                    var videos = items.Select(ReadVideo).ToList();
                    // With "next", the first video of the request must play first.
                    foreach (var video in Enumerable.Reverse(videos).Where(video => video.Next))
                        playlist.Add(video.Source, video.Seconds, video.Loop, next: true);
                    foreach (var video in videos.Where(video => !video.Next))
                        playlist.Add(video.Source, video.Seconds, video.Loop, next: false);
                    break;
                }
                case ("POST", "genlock/next"):
                    playlist.Skip();
                    break;
                case ("POST", "genlock/stop"):
                    playlist.Clear();
                    playlist.Skip();
                    break;
                case ("DELETE", "genlock/queue"):
                    playlist.Clear();
                    break;
                case ("DELETE", var path) when path.StartsWith("genlock/queue/"):
                    if (!int.TryParse(path["genlock/queue/".Length..], out var id) || !playlist.Remove(id))
                    {
                        SendError(response, 404, "The queue does not have this video.");
                        return;
                    }

                    break;
                default:
                    SendError(response, 404, "Use GET /genlock, POST /genlock/queue, POST /genlock/next, " +
                                             "POST /genlock/stop or DELETE /genlock/queue[/id].");
                    return;
            }

            Send(response, "application/json", Encoding.UTF8.GetBytes(playlist.ToJson()));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            SendError(response, 400, e.Message);
        }
    }

    private readonly record struct Video(string Source, double? Seconds, bool Loop, bool Next);

    /// <summary>Reads a video of a request. A source without "://" must be a file that exists.</summary>
    /// <exception cref="FormatException">The video is not correct.</exception>
    private static Video ReadVideo(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("A video must be a JSON object, for example {\"source\": \"movie.mp4\"}.");
        var source = element.TryGetProperty("source", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException("A video needs \"source\": a file or a URL.");
        if (!source.Contains("://"))
        {
            if (!File.Exists(source))
                throw new FormatException($"The file {source} does not exist.");
            source = Path.GetFullPath(source);
        }

        double? seconds = element.TryGetProperty("seconds", out value) && value.ValueKind != JsonValueKind.Null
            ? value.GetDouble()
            : null;
        if (seconds is <= 0)
            throw new FormatException("\"seconds\" must be more than 0.");
        var loop = element.TryGetProperty("loop", out value) && value.GetBoolean();
        var next = element.TryGetProperty("next", out value) && value.GetBoolean();
        return new Video(source, seconds, loop, next);
    }

    private static void SendError(HttpListenerResponse response, int status, string message)
    {
        response.StatusCode = status;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("error", message);
            json.WriteEndObject();
        }

        Send(response, "application/json", buffer.ToArray());
    }

    private static void Send(HttpListenerResponse response, string type, byte[] body)
    {
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
        response.Close();
    }
}
