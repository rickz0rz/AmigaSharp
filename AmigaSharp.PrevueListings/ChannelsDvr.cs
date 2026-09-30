using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmigaSharp.PrevueListings;

/// <summary>A channel of the guide of a Channels DVR server.</summary>
public sealed record GuideChannel(
    [property: JsonPropertyName("Number")] string Number,
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("CallSign")] string? CallSign,
    [property: JsonPropertyName("HD")] bool HD,
    [property: JsonPropertyName("Hidden")] bool Hidden,
    [property: JsonPropertyName("Image")] string? Image = null);

/// <summary>
/// A program on a channel of the guide. <see cref="Time"/> is a Unix time, and the duration is in seconds. The content
/// rating is for example "TV-PG" or "PG-13".
/// </summary>
public sealed record GuideAiring(
    [property: JsonPropertyName("Time")] long Time,
    [property: JsonPropertyName("Duration")] long Duration,
    [property: JsonPropertyName("Title")] string? Title,
    [property: JsonPropertyName("Categories")] string[]? Categories,
    [property: JsonPropertyName("Tags")] string[]? Tags,
    [property: JsonPropertyName("Summary")] string? Summary = null,
    [property: JsonPropertyName("ContentRating")] string? ContentRating = null,
    [property: JsonPropertyName("ReleaseYear")] int? ReleaseYear = null);

public sealed record GuideEntry(
    [property: JsonPropertyName("Channel")] GuideChannel Channel,
    [property: JsonPropertyName("Airings")] GuideAiring[] Airings);

/// <summary>The JSON types of the guide. The serializer code is generated at build time, as Native AOT needs.</summary>
[JsonSerializable(typeof(GuideEntry[]))]
internal sealed partial class GuideJsonContext : JsonSerializerContext;

/// <summary>Reads the guide of a Channels DVR server with its HTTP API.</summary>
public sealed class ChannelsDvrClient(Uri server, bool acceptAnyCertificate = false) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        // A Channels DVR server on the local network often has a certificate that does not match its address.
        ServerCertificateCustomValidationCallback = acceptAnyCertificate
            ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            : null,
    })
    {
        BaseAddress = server,
        Timeout = TimeSpan.FromMinutes(2),
    };

    /// <summary>GET /devices/ANY/guide: the channels and their programs from a time, for a duration.</summary>
    public async Task<GuideEntry[]> GetGuideAsync(DateTimeOffset start, TimeSpan duration)
    {
        var path = $"devices/ANY/guide?time={start.ToUnixTimeSeconds()}&duration={(long)duration.TotalSeconds}";
        await using var stream = await _http.GetStreamAsync(path);
        return await JsonSerializer.DeserializeAsync(stream, GuideJsonContext.Default.GuideEntryArray) ?? [];
    }

    public void Dispose() => _http.Dispose();
}
