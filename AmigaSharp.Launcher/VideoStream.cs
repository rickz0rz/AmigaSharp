using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Launcher;

/// <summary>
/// Streams the display as a live HLS video over HTTP, for example as a custom channel of Channels DVR. ffmpeg encodes
/// the pictures to H.264 with a silent AAC audio track, and writes segments of 2 seconds. An HTTP server gives the
/// segments, the playlist of the stream (/stream.m3u8) and an M3U playlist of one channel (/channels.m3u).
/// </summary>
/// <remarks>
/// The stream has 29.97 pictures each second, as NTSC video. A thread takes the last complete picture of the display
/// at that rate, so the emulation never waits for the encoder. The picture is 4:3: 960 by 720 pixels, or in a 1280 by
/// 720 picture with black bars at the sides for a 16:9 screen.
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
    private volatile bool _stopped;

    /// <param name="port">The TCP port of the HTTP server. It listens on all the addresses of the host.</param>
    /// <param name="wide">True to add black bars for a 16:9 picture.</param>
    public VideoStream(Display display, int port, bool wide, string channelName, TextWriter log)
    {
        _display = display;
        _log = log;
        _channelName = channelName;

        var filter = wide ? "scale=960:720:flags=lanczos,pad=1280:720:160:0,setsar=1" : "scale=960:720:flags=lanczos,setsar=1";
        var arguments = new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{Display.Width}x{Display.Height}", "-framerate", "30000/1001",
            "-i", "pipe:0",
            "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
            "-map", "0:v", "-map", "1:a",
            "-vf", filter,
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
            "-g", "60", "-b:v", "4M", "-maxrate", "4M", "-bufsize", "8M",
            "-c:a", "aac", "-b:a", "64k",
            "-f", "hls", "-hls_time", "2", "-hls_list_size", "10",
            "-hls_flags", "delete_segments+independent_segments",
            Path.Combine(_directory, PlaylistName),
        };
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        _ffmpeg = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");

        _http.Prefixes.Add($"http://*:{port}/");
        _http.Start();
        new Thread(SendFrames) { IsBackground = true, Name = "Video stream" }.Start();
        new Thread(Serve) { IsBackground = true, Name = "Video stream HTTP" }.Start();
        log.WriteLine($"Streaming on http://localhost:{port}/{PlaylistName}. The playlist of the channel is " +
                      $"http://<address of this host>:{port}/channels.m3u.");
    }

    public void Dispose()
    {
        _stopped = true;
        _http.Close();
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

    private static void Send(HttpListenerResponse response, string type, byte[] body)
    {
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
        response.Close();
    }
}
