using System.Text.Json;

namespace AmigaSharp.Host;

/// <summary>
/// Reads the items of a queue (the genlock or the music), from a request of the HTTP server or from a queue file.
/// </summary>
public static class QueueRequest
{
    /// <summary>An item of a request.</summary>
    /// <param name="Seconds">The time limit, or null to play to the end.</param>
    /// <param name="Next">True to put the item first in the queue.</param>
    public readonly record struct Video(string Source, double? Seconds, bool Loop, bool Next);

    /// <summary>The contents of a queue file: the loop setting (null to keep it) and the items.</summary>
    public sealed record QueueFile(bool? LoopAll, IReadOnlyList<Video> Videos);

    /// <summary>
    /// Reads an item. A source without "://" is a file that must exist, or <see cref="GenlockPlaylist.Black"/>, which
    /// needs "seconds".
    /// </summary>
    /// <param name="directory">The directory of a relative file. Null for the current directory.</param>
    /// <exception cref="FormatException">The item is not correct.</exception>
    public static Video ReadVideo(JsonElement element, string? directory = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("A video must be a JSON object, for example {\"source\": \"movie.mp4\"}.");
        var source = element.TryGetProperty("source", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException("A video needs \"source\": a file, a URL or \"black\".");
        double? seconds = element.TryGetProperty("seconds", out value) && value.ValueKind != JsonValueKind.Null
            ? value.GetDouble()
            : null;
        if (seconds is <= 0)
            throw new FormatException("\"seconds\" must be more than 0.");
        if (source == GenlockPlaylist.Black)
        {
            if (seconds == null)
                throw new FormatException("\"black\" needs \"seconds\".");
        }
        else if (!source.Contains("://"))
        {
            var path = Path.Combine(directory ?? "", source);
            if (!File.Exists(path))
                throw new FormatException($"The file {path} does not exist.");
            source = Path.GetFullPath(path);
        }

        var loop = element.TryGetProperty("loop", out value) && value.GetBoolean();
        var next = element.TryGetProperty("next", out value) && value.GetBoolean();
        return new Video(source, seconds, loop, next);
    }

    /// <summary>Reads the loop setting of a request: "all" or "off". Null if the request has none.</summary>
    /// <exception cref="FormatException">The setting is not correct.</exception>
    public static bool? ReadLoop(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("loop", out var loop))
            return null;
        return (loop.ValueKind == JsonValueKind.String ? loop.GetString() : null) switch
        {
            "all" => true,
            "off" => false,
            _ => throw new FormatException("\"loop\" must be \"all\" or \"off\"."),
        };
    }

    /// <summary>
    /// Reads a queue file: a JSON array of items, or an object with "loop" and "queue", for example
    /// {"loop": "all", "queue": [{"source": "promo.mp4"}, {"source": "black", "seconds": 180}]}. A relative file is
    /// relative to the directory of the queue file.
    /// </summary>
    /// <exception cref="FormatException">The file is not correct.</exception>
    public static QueueFile ReadFile(string path)
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
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("queue", out var queue) &&
                  queue.ValueKind == JsonValueKind.Array ? queue
                : throw new FormatException("A queue file is a JSON array of videos, or an object with \"queue\".");
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            return new QueueFile(ReadLoop(root), items.EnumerateArray().Select(item => ReadVideo(item, directory)).ToList());
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            throw new FormatException($"{path}: {e.Message}");
        }
    }
}
