using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AmigaSharp.PrevueListings;

/// <summary>Converts the guide of a Channels DVR server to the listings of a Prevue broadcast day.</summary>
public static partial class GuideConverter
{
    /// <summary>The broadcast day of Prevue starts at 5:00 AM: slot 1 is 5:00 to 5:29 AM.</summary>
    public static readonly TimeOnly DayStart = new(5, 0);

    /// <summary>
    /// ESQ changes its current day at 5:30 AM, when the half-hour slot becomes 2 (ESQDISP_DrawStatusBanner_Impl).
    /// Before that time, the current day is the broadcast day that started on the day before.
    /// </summary>
    public static readonly TimeOnly DayChange = new(5, 30);

    /// <summary>The broadcast day that ESQ uses as its current day at the time.</summary>
    public static DateOnly CurrentDay(DateTime time) =>
        DateOnly.FromDateTime(time.TimeOfDay < DayChange.ToTimeSpan() ? time.AddDays(-1) : time);

    private const int MaximumTextLength = 120;

    /// <summary>The start of the broadcast day, in the time zone.</summary>
    public static DateTimeOffset StartOf(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(DayStart);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>
    /// Makes the listings of the broadcast day from the HD channels of the guide. The channels are in the order of
    /// their numbers, and a maximum of 200 channels go into the day, because ESQ does not keep more. A channel is a
    /// premium channel if its number or its call sign is in <paramref name="premium"/>. The case of letters does not
    /// matter.
    /// </summary>
    /// <remarks>
    /// A program goes into the slot where it starts. A program that starts before the day and continues into it goes
    /// into slot 1. If a program starts at a time that is not the start of its slot, its text starts with the time,
    /// for example "( 3:25) NFL Football", as in the Prevue data. If two programs start in one slot, the grid shows the
    /// first. The times are the times of the zone: the files use the time zone '6', so ESQ does not change them.
    /// The text of a movie has the Prevue movie format (see <see cref="MovieText"/>).
    /// </remarks>
    public static PrevueDay Convert(IEnumerable<GuideEntry> guide, DateOnly date, TimeZoneInfo zone,
        int maximumChannels = PrevueDataFile.MaximumChannels, IEnumerable<string>? premium = null)
    {
        var premiumChannels = new HashSet<string>(premium ?? [], StringComparer.OrdinalIgnoreCase);
        var start = StartOf(date, zone);
        var channels = guide
            .Where(entry => entry.Channel is { HD: true, Hidden: false })
            .OrderBy(entry => ChannelKey(entry.Channel.Number))
            .Take(Math.Min(maximumChannels, PrevueDataFile.MaximumChannels))
            .Select(entry => new PrevueChannel(entry.Channel.Number, Label(entry.Channel), Programs(entry.Airings, start, zone),
                premiumChannels.Contains(entry.Channel.Number) || premiumChannels.Contains(entry.Channel.CallSign ?? ""),
                entry.Channel.Image))
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

            var movie = airing.Categories?.Contains("Movie") == true;
            var text = movie ? MovieText(airing) : Clean(airing.Title ?? "");
            var local = TimeZoneInfo.ConvertTime(begin, zone);
            if (begin > dayStart && local.Minute % 30 != 0)
                text = $"({local.ToString("%h", CultureInfo.InvariantCulture),2}:{local.Minute:00}) {text}";
            if (airing.Tags?.Contains("CC") == true)
                text += " |";
            programs[slot] = new PrevueProgram(slot, text, movie);
        }

        return programs.Values.OrderBy(p => p.Slot).ToList();
    }

    /// <summary>
    /// The text of a movie: the title in quotation marks, the year, the summary, and the rating, for example
    /// <c>"Casablanca" (1942) A cafe owner meets an old love. (PG)</c>. ESQ changes the rating to its symbol. The
    /// grid shows the title and the year on the first line, and the full text when the movie fills the 3 columns.
    /// </summary>
    private static string MovieText(GuideAiring airing)
    {
        // The guide gives the year in the title, for example "Casablanca (1942)". A quotation mark in the title
        // becomes an apostrophe, because ESQ finds the end of the title at the second quotation mark.
        var title = Clean(ReleaseYearSuffix().Replace(airing.Title ?? "", "")).Replace('"', '\'');
        var year = airing.ReleaseYear is > 0 ? airing.ReleaseYear
            : ReleaseYearSuffix().Match(airing.Title ?? "") is { Success: true } match ? int.Parse(match.Groups[1].Value)
            : null;
        var parts = new List<string> { $"\"{title}\"" };
        if (year != null)
            parts.Add($"({year})");
        var summary = Clean(airing.Summary ?? "");
        if (summary.Length > 0)
            parts.Add(summary);
        if (airing.ContentRating is { } rating && RatingTokens.Contains(rating))
            parts.Add($"({rating})");
        return string.Join(' ', parts);
    }

    /// <summary>The ratings that ESQ changes to a symbol of the Prevue font, when they are in parentheses.</summary>
    private static readonly HashSet<string> RatingTokens =
    [
        "R", "Adult", "PG", "NR", "PG-13", "G", "NC-17", "TV-Y", "TV-Y7", "TV-PG", "TV-G", "TV-M", "TV-MA", "TV-14",
    ];

    [GeneratedRegex(@"\s*\((\d{4})\)\s*$")]
    private static partial Regex ReleaseYearSuffix();

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
