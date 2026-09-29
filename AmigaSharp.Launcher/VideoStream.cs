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
/// the pictures to H.264 and the sound to AAC, and writes segments of 2 seconds. An HTTP server gives the segments,
/// the playlist of the stream (/stream.m3u8) and an M3U playlist of one channel (/channels.m3u).
/// </summary>
/// <remarks>
/// <para>
/// The stream has 29.97 pictures each second, as NTSC video. A thread takes the last complete picture of the display
/// at that rate, so the emulation never waits for the encoder. The picture is 4:3: 960 by 720 pixels, or in a 1280 by
/// 720 picture with black bars at the sides for a 16:9 screen.
/// </para>
/// <para>
/// For each picture, <see cref="StreamMixer"/> mixes the sound of 1/29.97 second: the sound of the current video of the
/// genlock, the music of the audio playlist, and the sound of the Amiga. The sound goes to the encoder with the
/// picture, so the two stay together.
/// </para>
/// <para>
/// With a genlock source, the stream works as the genlock of the Prevue machine: the video of the source shows where
/// the display has the genlock key (the pixels with alpha 0, see <see cref="Display"/>). <see cref="GenlockPlaylist"/>
/// gives the video: a queue of files and URLs that the HTTP server can change (see <see cref="AnswerQueue"/>). The
/// sender puts the video under the display, one picture of the video for each picture of the stream (see
/// <see cref="SendFrames"/>). With no video in the queue, the display shows over black. Without the genlock, the
/// pixels of the genlock key show their own color.
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
    private readonly GenlockPlaylist _music;
    private readonly GenlockPlaylist? _genlock;
    private readonly StreamMixer _mixer;
    private readonly ControlLineFeed? _controlLine;

    // The sound goes to the encoder through this port. The sender adds the sound of each picture to the plan: the
    // sound of its video (or null), and if the video has sound.
    private readonly TcpListener _audioPort = new(IPAddress.Loopback, 0);
    private readonly BlockingCollection<(PcmBuffer? Audio, bool HasSound)> _audioPlan = new();
    private volatile bool _stopped;


    /// <param name="port">The TCP port of the HTTP server. It listens on all the addresses of the host.</param>
    /// <param name="wide">True to add black bars for a 16:9 picture.</param>
    /// <param name="audioPlaylist">
    /// A playlist or a directory of audio files (see <see cref="PlaylistFile"/>) for the music queue, which plays it in
    /// a loop. Null for an empty music queue.
    /// </param>
    /// <param name="genlockSource">
    /// The first video of the genlock playlist: a file (in a loop) or a URL, without a time limit. Null for none.
    /// </param>
    /// <param name="genlock">True for the genlock playlist, also without a first video.</param>
    /// <param name="amigaSound">The sound of the Amiga, or null for none.</param>
    /// <param name="controlLine">The control line of Prevue for the requests of /ctrl, or null for none.</param>
    public VideoStream(Display display, int port, bool wide, string channelName, TextWriter log,
        string? audioPlaylist = null, string? genlockSource = null, bool genlock = false, AudioTap? amigaSound = null,
        ControlLineFeed? controlLine = null)
    {
        _display = display;
        _controlLine = controlLine;
        _log = log;
        _channelName = channelName;
        // The music is a playlist of sound only. It always exists, so that the HTTP server can fill it.
        _music = new GenlockPlaylist(withAudio: true, log,
            item => new GenlockDecoder(item.Source, item.Loop, withAudio: true, log, pictures: false), name: "Music");
        if (audioPlaylist != null)
        {
            var files = PlaylistFile.Read(audioPlaylist);
            if (files.Count == 0)
                log.WriteLine($"The audio playlist {audioPlaylist} has no audio files.");
            foreach (var file in files)
                _music.Add(file, seconds: null, loop: false, next: false);
            _music.LoopAll = true;
        }

        _mixer = new StreamMixer(_music, amigaSound);
        if (genlock || genlockSource != null)
        {
            _genlock = new GenlockPlaylist(withAudio: true, log);
            if (genlockSource != null)
                _genlock.Add(genlockSource, seconds: null, loop: File.Exists(genlockSource), next: false);
        }

        _audioPort.Start();
        new Thread(SendAudio) { IsBackground = true, Name = "Stream audio" }.Start();
        string[] audioInput =
        [
            "-thread_queue_size", "1024", "-f", "s16le", "-ar", StreamMixer.SampleRate.ToString(), "-ac", "2",
            "-i", $"tcp://127.0.0.1:{((IPEndPoint)_audioPort.LocalEndpoint).Port}",
        ];

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
            // The stream ends with the pictures of the display, so that ffmpeg stops when the launcher stops, also when
            // the launcher is killed.
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
        _audioPlan.CompleteAdding();
        _audioPort.Stop();
        _genlock?.Dispose();
        _music.Dispose();
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

    /// <summary>
    /// Sends the pictures of the display, and the plan of their sound, at the frame rate of the stream. With the genlock,
    /// the display shows over the pictures of its playlist. While a video plays, the clock of the stream follows it: the
    /// time to the next picture is a little shorter when its decoder has more pictures than the target, and a little
    /// longer when it has fewer. So the stream takes each picture of the video once, and the picture of the display at
    /// an even rate. With no picture of a video, the display shows over black.
    /// </summary>
    private void SendFrames()
    {
        var playlist = _genlock;
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
            var frame = playlist?.TakeFrame(TimeSpan.FromMilliseconds(200));
            _display.CopyFrame(amiga);
            Composite(amiga, frame?.Pixels, output, genlock: playlist != null);
            if (frame is { } taken)
                GenlockDecoder.Return(taken.Pixels);
            _audioPlan.Add((frame?.Audio, frame?.HasSound ?? false));

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

            var correction = playlist is { IsPlaying: true }
                ? Math.Clamp(0.002 * (playlist.BufferedFrames - GenlockPlaylist.TargetFrames), -0.02, 0.02)
                : 0;
            next += 1 / FramesPerSecond * (1 - correction);
            // After a pause, do not send the missed pictures in a burst.
            if (clock.Elapsed.TotalSeconds - next > 1)
                next = clock.Elapsed.TotalSeconds;
        }
    }

    /// <summary>
    /// Puts the video under the display: where a pixel of the display has alpha 0 (the genlock key), the video shows,
    /// or black without a video. A pixel with an alpha between 0 and $FF, from the blend of two fields, mixes the two.
    /// Without the genlock, each pixel shows its own color.
    /// </summary>
    public static void Composite(uint[] amiga, uint[]? video, uint[] output, bool genlock)
    {
        if (!genlock)
        {
            for (var i = 0; i < output.Length; i++)
                output[i] = amiga[i] | 0xFF00_0000;
            return;
        }

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
    /// Sends the sound to the encoder: for each picture of the stream, the mix of 1/29.97 second (1601 or 1602 samples).
    /// </summary>
    private void SendAudio()
    {
        try
        {
            using var client = _audioPort.AcceptTcpClient();
            var output = client.GetStream();
            var samples = new short[StreamMixer.SampleRate / 20 * 2];
            long frame = 0;
            foreach (var (audio, hasSound) in _audioPlan.GetConsumingEnumerable())
            {
                var count = (long)((frame + 1) * StreamMixer.SampleRate / FramesPerSecond)
                            - (long)(frame * StreamMixer.SampleRate / FramesPerSecond);
                frame++;
                var span = samples.AsSpan(0, (int)count * 2);
                _mixer.Mix(span, audio, hasSound);
                output.Write(MemoryMarshal.AsBytes(span));
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
                AnswerQueue(context, _genlock, "genlock", name);
                return;
            }

            if (name == "music" || name.StartsWith("music/"))
            {
                AnswerQueue(context, _music, "music", name);
                return;
            }

            if (name == "mixer" || name.StartsWith("mixer/"))
            {
                AnswerMixer(context, name);
                return;
            }

            if (_controlLine != null && (name == "ctrl" || name.StartsWith("ctrl/")))
            {
                AnswerControl(context, _controlLine, name);
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
    /// Answers a request that controls a playlist: the genlock (/genlock) or the music (/music). Each answer that
    /// succeeds has the playlist as JSON.
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
    /// <item>POST /genlock with {"loop": "all"} or {"loop": "off"}: plays the queue in a loop, or once.</item>
    /// </list>
    /// The requests of /music are the same.
    /// </remarks>
    private static void AnswerQueue(HttpListenerContext context, GenlockPlaylist playlist, string prefix, string name)
    {
        var method = context.Request.HttpMethod;
        var response = context.Response;
        try
        {
            var path = name.TrimEnd('/');
            var action = path == prefix ? "" : path[(prefix.Length + 1)..];
            switch (method, action)
            {
                case ("GET", "" or "queue"):
                    break;
                case ("POST", ""):
                {
                    using var document = ReadJson(context);
                    if (document.RootElement.TryGetProperty("loop", out var loop))
                    {
                        playlist.LoopAll = loop.GetString() switch
                        {
                            "all" => true,
                            "off" => false,
                            _ => throw new FormatException("\"loop\" must be \"all\" or \"off\"."),
                        };
                    }

                    break;
                }
                case ("POST", "queue"):
                {
                    using var document = ReadJson(context);
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
                case ("POST", "next"):
                    playlist.Skip();
                    break;
                case ("POST", "stop"):
                    playlist.Stop();
                    break;
                case ("DELETE", "queue"):
                    playlist.Clear();
                    break;
                case ("DELETE", var item) when item.StartsWith("queue/"):
                    if (!int.TryParse(item["queue/".Length..], out var id) || !playlist.Remove(id))
                    {
                        SendError(response, 404, "The queue does not have this item.");
                        return;
                    }

                    break;
                default:
                    SendError(response, 404, $"Use GET /{prefix}, POST /{prefix}, POST /{prefix}/queue, POST /{prefix}/next, " +
                                             $"POST /{prefix}/stop or DELETE /{prefix}/queue[/id].");
                    return;
            }

            Send(response, "application/json", Encoding.UTF8.GetBytes(playlist.ToJson()));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            SendError(response, 400, e.Message);
        }
    }

    /// <summary>
    /// Answers a request that controls the mixer of the sound. Each answer that succeeds has the mixer as JSON.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>GET /mixer: the settings and the level of each layer (video, music, amiga), and the duck settings.</item>
    /// <item>
    /// POST /mixer/video, /mixer/music or /mixer/amiga: changes a layer, as JSON: {"volume": 0.5, "muted": false,
    /// "fade": 2}. Each value is optional. "volume" is 0 or more (1 is the normal level). "fade" is in seconds.
    /// </item>
    /// <item>
    /// POST /mixer/duck: changes the duck of the music, as JSON: {"when": "video-has-sound" or "never", "volume": 0.2,
    /// "fade": 0.5}. Each value is optional. "volume" is the part of its volume that the music keeps: 0 stops it.
    /// </item>
    /// </list>
    /// </remarks>
    private void AnswerMixer(HttpListenerContext context, string name)
    {
        var method = context.Request.HttpMethod;
        var response = context.Response;
        try
        {
            var path = name.TrimEnd('/');
            switch (method, path)
            {
                case ("GET", "mixer"):
                    break;
                case ("POST", "mixer/duck"):
                {
                    using var document = ReadJson(context);
                    var root = document.RootElement;
                    var duck = _mixer.Duck;
                    if (root.TryGetProperty("when", out var when))
                    {
                        duck = duck with
                        {
                            When = when.GetString() switch
                            {
                                "video-has-sound" => DuckCondition.VideoHasSound,
                                "never" => DuckCondition.Never,
                                _ => throw new FormatException("\"when\" must be \"video-has-sound\" or \"never\"."),
                            },
                        };
                    }

                    duck = duck with
                    {
                        Volume = ReadNumber(root, "volume", duck.Volume, 0, 1),
                        Fade = ReadNumber(root, "fade", duck.Fade, 0, 60),
                    };
                    _mixer.Duck = duck;
                    break;
                }
                case ("POST", var layerPath) when layerPath.StartsWith("mixer/"):
                {
                    var layer = layerPath["mixer/".Length..] switch
                    {
                        "video" => MixerLayer.Video,
                        "music" => MixerLayer.Music,
                        "amiga" => MixerLayer.Amiga,
                        _ => (MixerLayer?)null,
                    };
                    if (layer == null)
                    {
                        SendError(response, 404, "The layers are video, music and amiga.");
                        return;
                    }

                    using var document = ReadJson(context);
                    var root = document.RootElement;
                    var settings = _mixer.Get(layer.Value);
                    settings = settings with
                    {
                        Volume = ReadNumber(root, "volume", settings.Volume, 0, 4),
                        Muted = root.TryGetProperty("muted", out var muted) ? muted.GetBoolean() : settings.Muted,
                        Fade = ReadNumber(root, "fade", settings.Fade, 0, 60),
                    };
                    _mixer.Set(layer.Value, settings);
                    break;
                }
                default:
                    SendError(response, 404, "Use GET /mixer, POST /mixer/video, POST /mixer/music, POST /mixer/amiga " +
                                             "or POST /mixer/duck.");
                    return;
            }

            Send(response, "application/json", Encoding.UTF8.GetBytes(MixerJson()));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            SendError(response, 400, e.Message);
        }
    }

    /// <summary>Answers a request for the control line of Prevue (see docs/ctrl-line.md).</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>GET /ctrl: the bytes that wait for the line, and the seconds that the line needs to send them.</item>
    /// <item>
    /// POST /ctrl/promo: shows a promo, as JSON: {"title": "Seinfeld", "channels": "*", "brush": "AT"}, or
    /// {"right": {...}, "left": {...}, "first": "right"} (see <see cref="ControlLineFeed.Promo"/>).
    /// </item>
    /// <item>POST /ctrl/clear: removes the promo, so that the genlock video shows in the top half.</item>
    /// <item>POST /ctrl/default: shows the default brush in the top half.</item>
    /// <item>POST /ctrl/packets: sends raw packets, as JSON: [{"type": 1, "body": "3"}].</item>
    /// </list>
    /// The line sends 11 bytes each second, so a promo takes about 2 seconds. Each answer is the answer of GET /ctrl.
    /// </remarks>
    private static void AnswerControl(HttpListenerContext context, ControlLineFeed line, string name)
    {
        var response = context.Response;
        try
        {
            switch (context.Request.HttpMethod, name.TrimEnd('/'))
            {
                case ("GET", "ctrl"):
                    break;
                case ("POST", "ctrl/promo"):
                {
                    using var document = ReadJson(context);
                    line.Add(ControlLineFeed.Promo(document.RootElement));
                    break;
                }
                case ("POST", "ctrl/clear"):
                    line.Add([ControlLineFeed.Packet(1, "3")]);
                    break;
                case ("POST", "ctrl/default"):
                    line.Add([ControlLineFeed.Packet(1, "D")]);
                    break;
                case ("POST", "ctrl/packets"):
                {
                    using var document = ReadJson(context);
                    line.Add(ControlLineFeed.Packets(document.RootElement));
                    break;
                }
                default:
                    SendError(response, 404, "Use GET /ctrl, POST /ctrl/promo, POST /ctrl/clear, POST /ctrl/default " +
                                             "or POST /ctrl/packets.");
                    return;
            }

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                var queued = line.Queued;
                json.WriteStartObject();
                json.WriteNumber("queued", queued);
                json.WriteNumber("seconds", Math.Round(queued / ControlLineFeed.BytesPerSecond, 1));
                json.WriteNumber("sent", line.Sent);
                json.WriteEndObject();
            }

            Send(response, "application/json", buffer.ToArray());
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            SendError(response, 400, e.Message);
        }
    }

    /// <summary>The settings and the levels of the mixer, as JSON.</summary>
    private string MixerJson()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartObject("layers");
            foreach (var layer in Enum.GetValues<MixerLayer>())
            {
                var settings = _mixer.Get(layer);
                json.WriteStartObject(layer.ToString().ToLowerInvariant());
                json.WriteNumber("volume", settings.Volume);
                json.WriteBoolean("muted", settings.Muted);
                json.WriteNumber("fade", settings.Fade);
                json.WriteNumber("level", Math.Round(_mixer.Level(layer), 3));
                json.WriteEndObject();
            }

            json.WriteEndObject();
            var duck = _mixer.Duck;
            json.WriteStartObject("duck");
            json.WriteString("when", duck.When == DuckCondition.VideoHasSound ? "video-has-sound" : "never");
            json.WriteNumber("volume", duck.Volume);
            json.WriteNumber("fade", duck.Fade);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonDocument ReadJson(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    /// <summary>Reads an optional number of a request, in its range.</summary>
    /// <exception cref="FormatException">The number is not a number or is out of its range.</exception>
    private static double ReadNumber(JsonElement root, string name, double current, double minimum, double maximum)
    {
        if (!root.TryGetProperty(name, out var value))
            return current;
        var number = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN;
        if (double.IsNaN(number) || number < minimum || number > maximum)
            throw new FormatException($"\"{name}\" must be a number from {minimum} to {maximum}.");
        return number;
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
