using System.Text.Json;
using AmigaSharp.Host;

namespace AmigaSharp.PrevueLauncher;

/// <summary>
/// The key "top" of a segment of a schedule: what the top half of the screen of Prevue shows while the segment plays.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>"clear": the genlock video shows in the top half during all the segment.</item>
/// <item>"logos": the logo rotation of ESQ. The schedule sends nothing.</item>
/// <item>
/// A list of cues, in order: {"promo": "Seinfeld", "seconds": 30}, {"logo": "Insider", "seconds": 30}, or
/// {"clear": true}. A promo is a title, or an object as for POST /prevue/ctrl/promo. A logo is a name of LOGO.LST, or
/// null for the loaded logo. The last cue can have no "seconds": it stays until the end of the segment. After a cue
/// with "seconds", the top half is clear.
/// </item>
/// </list>
/// A clear top half gets a clear command each minute, so the logo rotation of ESQ does not start. The schedule
/// chooses each named logo before the logo before it shows (see <see cref="PrevueState.SetNextLine"/>). If the first
/// named logo is not the loaded logo when the schedule starts, the schedule shows the loaded logo for a moment and
/// removes it, so that ESQ loads the first named logo.
/// </remarks>
public sealed class PrevueSchedule(ControlLineRequests requests, PrevueState? state, TextWriter log)
    : IScheduleExtension
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private List<string> _logoPlan = [];
    private int _logoIndex;
    private Priming _priming = Priming.NotStarted;
    private DateTime _lastCommand = DateTime.MinValue;

    private enum Priming { NotStarted, Waiting, Done }

    public IReadOnlyCollection<string> Keys { get; } = ["top"];

    public void Check(string key, JsonElement value) => ReadTop(value);

    public void Prepare(Schedule schedule)
    {
        var plan = new List<string>();
        foreach (var segment in schedule.Segments)
        {
            if (!segment.Keys.TryGetValue("top", out var top))
                continue;
            foreach (var cue in ReadTop(top).Cues)
            {
                if (cue is { Kind: CueKind.Logo, Logo: { } name })
                    plan.Add(name);
            }
        }

        if (plan.Count > 0 && state == null)
            log.WriteLine("Schedule: the launcher does not know this ESQ, so it cannot choose the logos by name.");
        _logoPlan = plan;
        _priming = plan.Count > 0 && state != null ? Priming.NotStarted : Priming.Done;
    }

    public IScheduleCue? Start(ScheduleSegment segment) =>
        segment.Keys.TryGetValue("top", out var top) ? new TopCue(this, ReadTop(top)) : null;

    private enum CueKind { Promo, Logo, Clear }

    private sealed record Cue(CueKind Kind, double? Seconds, List<byte[]>? Promo = null, string? Logo = null);

    /// <summary>The top half of a segment: the rotation of ESQ, or cues (a clear top half is one clear cue).</summary>
    private sealed record Top(bool Rotation, List<Cue> Cues);

    /// <summary>Reads the value of "top".</summary>
    /// <exception cref="FormatException">The value is not correct.</exception>
    private static Top ReadTop(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() switch
            {
                "clear" => new Top(false, [new Cue(CueKind.Clear, null)]),
                "logos" => new Top(true, []),
                _ => throw new FormatException("\"top\" is \"clear\", \"logos\", or a list of cues."),
            };
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            throw new FormatException("\"top\" is \"clear\", \"logos\", or a list of cues.");
        var cues = new List<Cue>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new FormatException("A cue of \"top\" is a JSON object, for example {\"promo\": \"Seinfeld\"}.");
            double? seconds = null;
            if (element.TryGetProperty("seconds", out var time))
            {
                seconds = time.ValueKind == JsonValueKind.Number ? time.GetDouble() : 0;
                if (seconds <= 0)
                    throw new FormatException("\"seconds\" of a cue must be more than 0.");
            }
            else if (cues.Count + 1 < value.GetArrayLength())
            {
                throw new FormatException("Each cue of \"top\" needs \"seconds\", except the last cue.");
            }

            if (element.TryGetProperty("promo", out var promo))
            {
                // A title is the short form of {"title": "..."}.
                var packets = ControlLineFeed.Promo(
                    promo.ValueKind == JsonValueKind.String ? TitleRequest(promo.GetString()!) : promo);
                cues.Add(new Cue(CueKind.Promo, seconds, Promo: packets));
            }
            else if (element.TryGetProperty("logo", out var logo))
            {
                var name = logo.ValueKind switch
                {
                    JsonValueKind.String => logo.GetString(),
                    JsonValueKind.Null or JsonValueKind.True => null,
                    _ => throw new FormatException("\"logo\" is a name of LOGO.LST, or null for the loaded logo."),
                };
                cues.Add(new Cue(CueKind.Logo, seconds, Logo: name));
            }
            else if (element.TryGetProperty("clear", out _))
            {
                cues.Add(new Cue(CueKind.Clear, seconds));
            }
            else
            {
                throw new FormatException("A cue of \"top\" needs \"promo\", \"logo\" or \"clear\".");
            }
        }

        return new Top(false, cues);
    }

    private static JsonElement TitleRequest(string title)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("title", title);
            json.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement;
    }

    private void Send(string request, IEnumerable<byte[]> packets)
    {
        requests.SendRequest(request, packets);
        _lastCommand = DateTime.UtcNow;
    }

    /// <summary>
    /// Makes the first named logo the loaded logo, if it is not. Returns true when the logos are ready for the plan.
    /// </summary>
    private bool Prime()
    {
        if (_priming == Priming.Done)
            return true;
        var loaded = state!.LoadedLogo;
        if (loaded == null)
            return false;
        var first = _logoPlan[0];
        if (_priming == Priming.NotStarted)
        {
            if (PrevueState.IsLogo(loaded, first))
            {
                _priming = Priming.Done;
                return true;
            }

            if (state.FindLogoLine(first) is not { } line)
            {
                log.WriteLine($"Schedule: LOGO.LST has no logo {first}. The logos show in the order of the file.");
                _priming = Priming.Done;
                return true;
            }

            log.WriteLine($"Schedule: the loaded logo is {loaded}, so the schedule shows it for a moment to load {first}.");
            state.SetNextLine(line);
            Send("logo", [ControlLineFeed.Packet(1, "D")]);
            Send("clear", [ControlLineFeed.Packet(1, "3")]);
            _priming = Priming.Waiting;
            return false;
        }

        if (PrevueState.IsLogo(loaded, first))
            _priming = Priming.Done;
        return _priming == Priming.Done;
    }

    /// <summary>Shows a logo cue: it chooses the logo of the next named cue, and then it shows the loaded logo.</summary>
    private void ShowLogo(string? name)
    {
        if (name != null && state != null && _logoPlan.Count > 0)
        {
            var loaded = state.LoadedLogo;
            if (loaded != null && !PrevueState.IsLogo(loaded, name))
                log.WriteLine($"Schedule: the loaded logo is {loaded}, not {name}.");
            _logoIndex = (_logoIndex + 1) % _logoPlan.Count;
            if (state.FindLogoLine(_logoPlan[_logoIndex]) is { } next)
                state.SetNextLine(next);
        }

        Send("logo", [ControlLineFeed.Packet(1, "D")]);
    }

    /// <summary>The cues of the top half of one segment.</summary>
    private sealed class TopCue(PrevueSchedule schedule, Top top) : IScheduleCue
    {
        private int _next;
        private double _nextStart;
        private bool _hold;

        public void Tick(TimeSpan elapsed)
        {
            lock (schedule._lock)
            {
                if (top.Rotation)
                    return;
                if (!schedule.Prime())
                    return;
                while (_next < top.Cues.Count && elapsed.TotalSeconds >= _nextStart)
                {
                    var cue = top.Cues[_next++];
                    switch (cue.Kind)
                    {
                        case CueKind.Promo:
                            schedule.Send("promo", cue.Promo!);
                            break;
                        case CueKind.Logo:
                            schedule.ShowLogo(cue.Logo);
                            break;
                        case CueKind.Clear:
                            schedule.Send("clear", [ControlLineFeed.Packet(1, "3")]);
                            break;
                    }

                    _hold = cue.Kind == CueKind.Clear;
                    if (cue.Seconds is { } seconds)
                    {
                        _nextStart += seconds;
                    }
                    else
                    {
                        _nextStart = double.MaxValue;
                    }
                }

                // After the last cue with a time, the top half is clear.
                if (_next == top.Cues.Count && _nextStart != double.MaxValue && elapsed.TotalSeconds >= _nextStart &&
                    !_hold)
                {
                    schedule.Send("clear", [ControlLineFeed.Packet(1, "3")]);
                    _hold = true;
                }

                if (_hold && DateTime.UtcNow - schedule._lastCommand >= HoldTime)
                    schedule.Send("clear", [ControlLineFeed.Packet(1, "3")]);
            }
        }
    }
}
