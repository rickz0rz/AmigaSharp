using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using static AmigaSharp.Host.HttpJson;

namespace AmigaSharp.Host;

/// <summary>
/// A schedule of the stream: segments that play in order, optionally in a loop. Each segment has a genlock video or a
/// pause over black, and optionally settings of the music. An extension adds more keys, for example "top" for the top
/// half of the screen of Prevue.
/// </summary>
/// <remarks>
/// The file is JSON: {"loop": true, "segments": [{"video": "promo.mp4", "music": {"volume": 0.3}}, {"pause": 180}]}.
/// </remarks>
public sealed class Schedule
{
    private Schedule(bool loop, List<ScheduleSegment> segments)
    {
        Loop = loop;
        Segments = segments;
    }

    public bool Loop { get; }
    public IReadOnlyList<ScheduleSegment> Segments { get; }

    /// <summary>Reads a schedule file. A relative file is relative to the directory of the schedule file.</summary>
    /// <param name="extensions">The extensions that check their keys.</param>
    /// <exception cref="FormatException">The file is not correct.</exception>
    public static Schedule Read(string path, IReadOnlyList<IScheduleExtension> extensions)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FormatException($"{path}: {e.Message}");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return Parse(document.RootElement, Path.GetDirectoryName(Path.GetFullPath(path)), extensions);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            throw new FormatException($"{path}: {e.Message}");
        }
    }

    /// <summary>Reads a schedule from JSON.</summary>
    /// <exception cref="FormatException">The schedule is not correct.</exception>
    public static Schedule Parse(JsonElement root, string? directory, IReadOnlyList<IScheduleExtension> extensions)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("segments", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            throw new FormatException("A schedule is a JSON object with \"segments\", an array.");
        var loop = root.TryGetProperty("loop", out var value) && value.GetBoolean();
        var segments = new List<ScheduleSegment>();
        foreach (var element in list.EnumerateArray())
            segments.Add(ScheduleSegment.Parse(element, directory, extensions, segments.Count + 1));
        if (segments.Count == 0)
            throw new FormatException("A schedule needs one segment or more.");
        return new Schedule(loop, segments);
    }
}

/// <summary>A segment of a schedule: its genlock item, its music settings, and the keys of the extensions.</summary>
/// <param name="Number">The number of the segment in the schedule, from 1.</param>
/// <param name="Video">The genlock item: a video, or <see cref="GenlockPlaylist.Black"/> for a pause.</param>
/// <param name="Music">The settings of the music at the start of the segment, or null to keep them.</param>
/// <param name="Keys">The keys of the extensions, with their values.</param>
public sealed record ScheduleSegment(
    int Number, QueueRequest.Video Video, LayerSettings? Music, IReadOnlyDictionary<string, JsonElement> Keys)
{
    internal static ScheduleSegment Parse(JsonElement element, string? directory,
        IReadOnlyList<IScheduleExtension> extensions, int number)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException($"Segment {number} must be a JSON object.");
        var hasVideo = element.TryGetProperty("video", out var video);
        var hasPause = element.TryGetProperty("pause", out var pause);
        if (hasVideo == hasPause)
            throw new FormatException($"Segment {number} needs \"video\" or \"pause\", not the two.");

        QueueRequest.Video item;
        try
        {
            if (hasPause)
            {
                var seconds = pause.ValueKind == JsonValueKind.Number ? pause.GetDouble() : 0;
                if (seconds <= 0)
                    throw new FormatException("\"pause\" is the seconds of the pause, more than 0.");
                item = new QueueRequest.Video(GenlockPlaylist.Black, seconds, false, false);
            }
            else
            {
                // The video has the values of a request of the queue: "seconds" and "loop" are optional.
                using var request = JsonDocument.Parse(VideoRequest(element, video));
                item = QueueRequest.ReadVideo(request.RootElement, directory);
            }
        }
        catch (FormatException e)
        {
            throw new FormatException($"Segment {number}: {e.Message}");
        }

        LayerSettings? music = null;
        if (element.TryGetProperty("music", out var settings))
        {
            if (settings.ValueKind != JsonValueKind.Object)
                throw new FormatException($"Segment {number}: \"music\" must be a JSON object, for example {{\"volume\": 0.3}}.");
            var current = new LayerSettings();
            music = current with
            {
                Volume = ReadNumber(settings, "volume", current.Volume, 0, 4),
                Muted = settings.TryGetProperty("muted", out var muted) && muted.GetBoolean(),
                Fade = ReadNumber(settings, "fade", current.Fade, 0, 60),
            };
        }

        var keys = new Dictionary<string, JsonElement>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is "video" or "pause" or "seconds" or "loop" or "music")
                continue;
            var extension = extensions.FirstOrDefault(candidate => candidate.Keys.Contains(property.Name));
            if (extension == null)
                throw new FormatException($"Segment {number}: the launcher does not know \"{property.Name}\".");
            try
            {
                extension.Check(property.Name, property.Value);
            }
            catch (FormatException e)
            {
                throw new FormatException($"Segment {number}: {e.Message}");
            }

            keys[property.Name] = property.Value.Clone();
        }

        return new ScheduleSegment(number, item, music, keys);
    }

    private static string VideoRequest(JsonElement segment, JsonElement video)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WritePropertyName("source");
            video.WriteTo(json);
            if (segment.TryGetProperty("seconds", out var seconds))
            {
                json.WritePropertyName("seconds");
                seconds.WriteTo(json);
            }

            if (segment.TryGetProperty("loop", out var loop))
            {
                json.WritePropertyName("loop");
                loop.WriteTo(json);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>
/// Adds keys to the segments of a schedule, for example "top" for the top half of the screen of Prevue.
/// </summary>
public interface IScheduleExtension
{
    /// <summary>The keys of a segment that the extension handles.</summary>
    IReadOnlyCollection<string> Keys { get; }

    /// <summary>Checks the value of a key, when the launcher reads the schedule.</summary>
    /// <exception cref="FormatException">The value is not correct.</exception>
    void Check(string key, JsonElement value);

    /// <summary>Gets all the segments before the schedule starts, for example to plan the logos.</summary>
    void Prepare(Schedule schedule);

    /// <summary>A segment starts. The runner calls <see cref="IScheduleCue.Tick"/> of the result until it ends.</summary>
    /// <returns>The cue of the segment, or null if the segment has no key of the extension.</returns>
    IScheduleCue? Start(ScheduleSegment segment);
}

/// <summary>What an extension does while a segment plays.</summary>
public interface IScheduleCue
{
    /// <summary>The runner calls it often, with the time since the start of the segment.</summary>
    void Tick(TimeSpan elapsed);
}

/// <summary>
/// Plays a schedule on the genlock queue. The next segment goes into the queue when a segment starts, so the queue
/// prepares its video before the end of the current video. A segment starts when its video starts.
/// </summary>
public sealed class ScheduleRunner : IDisposable
{
    private static readonly TimeSpan TickTime = TimeSpan.FromMilliseconds(200);

    private readonly Schedule _schedule;
    private readonly GenlockPlaylist _genlock;
    private readonly StreamMixer _mixer;
    private readonly IReadOnlyList<IScheduleExtension> _extensions;
    private readonly TextWriter _log;
    private readonly Thread _thread;
    private readonly object _lock = new();
    private volatile bool _stopped;
    private int _current = -1;
    private readonly Stopwatch _segmentTime = new();
    private int _cycles;
    private bool _finished;

    public ScheduleRunner(Schedule schedule, GenlockPlaylist genlock, StreamMixer mixer,
        IReadOnlyList<IScheduleExtension> extensions, TextWriter log)
    {
        _schedule = schedule;
        _genlock = genlock;
        _mixer = mixer;
        _extensions = extensions;
        _log = log;
        foreach (var extension in extensions)
            extension.Prepare(schedule);
        _thread = new Thread(Run) { IsBackground = true, Name = "Schedule" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopped = true;
        _thread.Join();
    }

    /// <summary>The state of the schedule, as JSON.</summary>
    public string ToJson()
    {
        lock (_lock)
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteNumber("segments", _schedule.Segments.Count);
                json.WriteBoolean("loop", _schedule.Loop);
                if (_current >= 0)
                {
                    json.WriteNumber("segment", _schedule.Segments[_current].Number);
                    json.WriteNumber("elapsed", Math.Round(_segmentTime.Elapsed.TotalSeconds, 1));
                }
                else
                {
                    json.WriteNull("segment");
                }

                json.WriteNumber("cycles", _cycles);
                json.WriteBoolean("finished", _finished);
                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private void Run()
    {
        // The schedule controls the queue: its items end, and the next segment follows.
        _genlock.LoopAll = false;
        var next = 0;
        var nextId = Queue(next);
        List<IScheduleCue> cues = [];
        while (!_stopped)
        {
            if (nextId is { } id)
            {
                if (_genlock.CurrentId == id)
                {
                    cues = StartSegment(next);
                    next = NextIndex(next);
                    nextId = next >= 0 ? Queue(next) : null;
                }
                else if (!_genlock.Has(id))
                {
                    // The item ended without a picture, for example a file that does not play.
                    _log.WriteLine($"Schedule: segment {_schedule.Segments[next].Number} did not play.");
                    next = NextIndex(next);
                    nextId = next >= 0 ? Queue(next) : null;
                }
            }
            else if (_genlock.CurrentId == null)
            {
                lock (_lock)
                {
                    if (!_finished)
                        _log.WriteLine("Schedule: the schedule ended.");
                    _finished = true;
                    _current = -1;
                }

                cues = [];
            }

            var elapsed = _segmentTime.Elapsed;
            foreach (var cue in cues)
                cue.Tick(elapsed);
            Thread.Sleep(TickTime);
        }
    }

    private int? Queue(int index)
    {
        var video = _schedule.Segments[index].Video;
        return _genlock.Add(video.Source, video.Seconds, video.Loop, next: false).Id;
    }

    private int NextIndex(int index)
    {
        if (index + 1 < _schedule.Segments.Count)
            return index + 1;
        return _schedule.Loop ? 0 : -1;
    }

    private List<IScheduleCue> StartSegment(int index)
    {
        var segment = _schedule.Segments[index];
        lock (_lock)
        {
            if (index == 0 && _current >= 0)
                _cycles++;
            _current = index;
            _segmentTime.Restart();
        }

        _log.WriteLine($"Schedule: segment {segment.Number} starts.");
        if (segment.Music is { } music)
            _mixer.Set(MixerLayer.Music, music);
        var cues = new List<IScheduleCue>();
        foreach (var extension in _extensions)
        {
            if (extension.Start(segment) is { } cue)
                cues.Add(cue);
        }

        return cues;
    }
}

/// <summary>The request GET /schedule: the state of the schedule.</summary>
public sealed class ScheduleRequests : IStreamRequests
{
    /// <summary>The runner, when the schedule started.</summary>
    public ScheduleRunner? Runner { get; set; }

    public string Prefix => "schedule";

    public void Answer(HttpListenerContext context, string name)
    {
        if (context.Request.HttpMethod != "GET" || name.TrimEnd('/') != Prefix || Runner is not { } runner)
        {
            SendError(context.Response, 404, "Use GET /schedule.");
            return;
        }

        Send(context.Response, "application/json", Encoding.UTF8.GetBytes(runner.ToJson()));
    }
}
