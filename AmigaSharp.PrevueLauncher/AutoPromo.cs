using System.Text.Json;
using System.Text.RegularExpressions;
using AmigaSharp.Runtime;

namespace AmigaSharp.PrevueLauncher;

/// <summary>
/// What an automatic promo can show: {"movies": true, "premium": false, "channels": ["KTIV*", "4"], "titles":
/// ["Seinfeld"], "within": 3, "now": false, "repeat": 10, "order": "soonest"}. Each value is optional.
/// </summary>
/// <param name="Movies">True for movies only, false for no movies, null for all.</param>
/// <param name="Premium">True for premium channels only, false for no premium channels, null for all.</param>
/// <param name="Channels">Call letters (with "*" for any text) or channel numbers. Empty for all channels.</param>
/// <param name="Titles">Parts of titles. Empty for all titles.</param>
/// <param name="Within">The hours after now for the start of the program. The default is 3.</param>
/// <param name="Now">True to also show a program that plays now.</param>
/// <param name="Repeat">The number of the last automatic promos whose titles do not show again. The default is 10.</param>
/// <param name="Random">True to choose among the programs by chance ("order": "random"). False for the soonest
/// program ("order": "soonest", the default).</param>
public sealed record AutoCriteria(
    bool? Movies, bool? Premium, IReadOnlyList<string> Channels, IReadOnlyList<string> Titles, double Within, bool Now,
    int Repeat, bool Random = false)
{
    /// <summary>Reads the criteria of "auto".</summary>
    /// <exception cref="FormatException">The criteria are not correct.</exception>
    public static AutoCriteria Read(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.True)
            return new AutoCriteria(null, null, [], [], 3, false, 10);
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("\"auto\" is true, or an object, for example {\"movies\": true, \"within\": 3}.");
        var within = element.TryGetProperty("within", out var value) ? Number(value, "within", 0.5, 24) : 3;
        var repeat = element.TryGetProperty("repeat", out value) ? (int)Number(value, "repeat", 0, 100) : 10;
        var order = element.TryGetProperty("order", out value) ? value.GetString() : "soonest";
        if (order is not ("soonest" or "random"))
            throw new FormatException("\"order\" must be \"soonest\" or \"random\".");
        return new AutoCriteria(Flag(element, "movies"), Flag(element, "premium"), Texts(element, "channels"),
            Texts(element, "titles"), within, Flag(element, "now") == true, repeat, order == "random");
    }

    private static bool? Flag(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) ? null
        : value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"\"{name}\" must be true or false."),
        };

    private static double Number(JsonElement value, string name, double minimum, double maximum)
    {
        var number = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN;
        if (double.IsNaN(number) || number < minimum || number > maximum)
            throw new FormatException($"\"{name}\" must be a number from {minimum} to {maximum}.");
        return number;
    }

    private static List<string> Texts(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return [];
        if (value.ValueKind == JsonValueKind.String)
            return [value.GetString()!];
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new FormatException($"\"{name}\" must be text or a list of texts.");
        return value.EnumerateArray().Select(item => item.GetString()!).ToList();
    }

    /// <summary>True if the program fits the criteria, in the half hours from the current half hour.</summary>
    public bool Matches(GuideProgram program, int currentSlot)
    {
        var first = Now ? currentSlot : currentSlot + 1;
        var last = currentSlot + (int)Math.Round(Within * 2);
        if (program.Slot < first || program.Slot > last)
            return false;
        if (Movies is { } movies && program.Movie != movies)
            return false;
        if (Premium is { } premium && program.Premium != premium)
            return false;
        if (Channels.Count > 0 && !Channels.Any(channel =>
                string.Equals(channel, program.Number, StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(program.CallLetters, "^" + Regex.Escape(channel).Replace("\\*", ".*") + "$",
                    RegexOptions.IgnoreCase)))
            return false;
        return Titles.Count == 0 ||
               Titles.Any(title => program.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Chooses the program of an automatic promo from the listings of ESQ (see <see cref="PrevueGuide"/>): the first
/// program that fits the criteria, in the order of the time and of the channels (or in a random order), whose title
/// was not in a recent automatic promo. If all the titles were in recent promos, it chooses the title of the oldest
/// one.
/// </summary>
public sealed class AutoPromo(Memory memory, EsqVariables esq)
{
    private readonly object _lock = new();
    private readonly List<string> _recent = [];

    /// <summary>The current half hour of ESQ.</summary>
    public int CurrentSlot => PrevueGuide.CurrentSlot(memory, esq);

    /// <summary>The last program of an automatic promo, or null.</summary>
    public GuideProgram? LastPick { get; private set; }

    /// <summary>Chooses a program, or returns null if no program fits the criteria.</summary>
    public GuideProgram? Pick(AutoCriteria criteria)
    {
        var current = PrevueGuide.CurrentSlot(memory, esq);
        var candidates = PrevueGuide.Read(memory, esq)
            .Where(program => criteria.Matches(program, current))
            .OrderBy(program => program.Slot)
            .ThenBy(program => program.Channel)
            .ToList();
        if (criteria.Random)
            candidates = candidates.OrderBy(_ => System.Random.Shared.Next()).ToList();
        if (candidates.Count == 0)
            return null;
        lock (_lock)
        {
            var recent = _recent.TakeLast(criteria.Repeat).ToList();
            var pick = candidates.FirstOrDefault(program => !recent.Contains(Key(program))) ??
                       candidates.OrderBy(program => _recent.IndexOf(Key(program))).First();
            _recent.Remove(Key(pick));
            _recent.Add(Key(pick));
            if (_recent.Count > 100)
                _recent.RemoveAt(0);
            LastPick = pick;
            return pick;
        }
    }

    /// <summary>
    /// Makes the packets of a promo request with "auto": {"auto": {...}, "brush": "AT", "side": "left"}. "brush" and
    /// "side" (right, the default, or left) are optional.
    /// </summary>
    /// <exception cref="FormatException">The request is not correct, or no program fits the criteria.</exception>
    public List<byte[]> Packets(JsonElement request, out GuideProgram program)
    {
        var criteria = AutoCriteria.Read(request.GetProperty("auto"));
        var (side, brush) = Options(request);
        program = Pick(criteria) ?? throw new FormatException("No program of the listings fits \"auto\".");
        return ControlLineFeed.Promo(Request(program, side, brush));
    }

    /// <summary>Reads "side" and "brush" of a request with "auto".</summary>
    /// <exception cref="FormatException">A value is not correct.</exception>
    public static (string Side, string? Brush) Options(JsonElement request)
    {
        var side = request.TryGetProperty("side", out var value) ? value.GetString() : "right";
        if (side is not ("right" or "left"))
            throw new FormatException("\"side\" must be \"right\" or \"left\".");
        string? brush = null;
        if (request.TryGetProperty("brush", out value))
        {
            brush = value.GetString();
            if (brush is not { Length: 2 })
                throw new FormatException("A brush is an ID of 2 characters, for example \"AT\".");
        }

        return (side!, brush);
    }

    private static string Key(GuideProgram program) => program.Search.ToUpperInvariant();

    /// <summary>The promo request of a program: its title on its channel, on one side of the top half.</summary>
    private static JsonElement Request(GuideProgram program, string side, string? brush)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartObject(side);
            json.WriteString("title", program.Search);
            json.WriteString("channels", program.CallLetters.Length > 0 ? program.CallLetters : "*");
            if (brush != null)
                json.WriteString("brush", brush);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement;
    }
}
