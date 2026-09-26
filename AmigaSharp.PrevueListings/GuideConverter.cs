using System.Globalization;
using System.Text;

namespace AmigaSharp.PrevueListings;

/// <summary>Converts the guide of a Channels DVR server to the listings of a Prevue broadcast day.</summary>
public static class GuideConverter
{
    /// <summary>The broadcast day of Prevue starts at 5:00 AM.</summary>
    public static readonly TimeOnly DayStart = new(5, 0);

    private const int MaximumTextLength = 120;

    /// <summary>The start of the broadcast day, in the time zone.</summary>
    public static DateTimeOffset StartOf(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(DayStart);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>
    /// Makes the listings of the broadcast day from the HD channels of the guide. The channels are in the order of
    /// their numbers, and a maximum of 200 channels go into the day, because ESQ does not keep more.
    /// </summary>
    /// <remarks>
    /// A program goes into the slot where it starts. A program that starts before the day and continues into it goes
    /// into slot 1. If a program starts at a time that is not the start of its slot, its text starts with the time,
    /// for example "( 3:25) NFL Football", as in the Prevue data. If two programs start in one slot, the grid shows the
    /// first. The times are the times of the zone: the files use the time zone '6', so ESQ does not change them.
    /// </remarks>
    public static PrevueDay Convert(IEnumerable<GuideEntry> guide, DateOnly date, TimeZoneInfo zone)
    {
        var start = StartOf(date, zone);
        var channels = guide
            .Where(entry => entry.Channel is { HD: true, Hidden: false })
            .OrderBy(entry => ChannelKey(entry.Channel.Number))
            .Take(PrevueDataFile.MaximumChannels)
            .Select(entry => new PrevueChannel(entry.Channel.Number, Label(entry.Channel), Programs(entry.Airings, start, zone)))
            .ToList();
        return new PrevueDay(date, channels);
    }

    private static List<PrevueProgram> Programs(IEnumerable<GuideAiring> airings, DateTimeOffset dayStart, TimeZoneInfo zone)
    {
        var programs = new Dictionary<int, PrevueProgram>();
        foreach (var airing in airings.OrderBy(a => a.Time))
        {
            var begin = DateTimeOffset.FromUnixTimeSeconds(airing.Time);
            var end = begin.AddSeconds(airing.Duration);
            if (end <= dayStart || begin >= dayStart.AddDays(1))
                continue;

            var slot = begin <= dayStart ? 1 : (int)((begin - dayStart).TotalMinutes / 30) + 1;
            if (slot > PrevueDataFile.SlotsPerDay || programs.ContainsKey(slot))
                continue;

            var text = Clean(airing.Title ?? "");
            var local = TimeZoneInfo.ConvertTime(begin, zone);
            if (begin > dayStart && local.Minute % 30 != 0)
                text = $"({local.ToString("%h", CultureInfo.InvariantCulture),2}:{local.Minute:00}) {text}";
            if (airing.Tags?.Contains("CC") == true)
                text += " |";
            var movie = airing.Categories?.Contains("Movie") == true;
            programs[slot] = new PrevueProgram(slot, text, movie);
        }

        return programs.Values.OrderBy(p => p.Slot).ToList();
    }

    /// <summary>The call sign, or the name if the channel has no call sign.</summary>
    private static string Label(GuideChannel channel) =>
        Clean(string.IsNullOrWhiteSpace(channel.CallSign) ? channel.Name ?? channel.Number : channel.CallSign);

    /// <summary>Sorts "2.1" before "2.10" and "4" before "38".</summary>
    private static (int, int) ChannelKey(string number)
    {
        var parts = number.Split('.');
        return (int.TryParse(parts[0], out var major) ? major : int.MaxValue,
            parts.Length > 1 && int.TryParse(parts[1], out var minor) ? minor : 0);
    }

    /// <summary>
    /// Makes the text printable in the Prevue fonts: letters with accents lose the accents, other characters outside
    /// ASCII go away, and "|" (the closed-caption icon) becomes "/".
    /// </summary>
    private static string Clean(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (c == '|')
                result.Append('/');
            else if (c is >= ' ' and < '\x7F')
                result.Append(c);
            else if (c is '‘' or '’')
                result.Append('\'');
            else if (c is '“' or '”')
                result.Append('"');
            else if (c is '–' or '—')
                result.Append('-');
        }

        var cleaned = result.ToString().Trim();
        return cleaned.Length > MaximumTextLength ? cleaned[..MaximumTextLength] : cleaned;
    }
}
