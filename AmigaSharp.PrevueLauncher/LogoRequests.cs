using System.Net;
using System.Text;
using System.Text.Json;
using AmigaSharp.Host;
using AmigaSharp.Runtime;
using static AmigaSharp.Host.HttpJson;

namespace AmigaSharp.PrevueLauncher;

/// <summary>
/// The requests of /prevue/logos: the list of the logos, and the choice of the next logo.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>GET /prevue/logos: the logos, as in GET /prevue/state.</item>
/// <item>
/// POST /prevue/logos/next: makes a logo the next logo that ESQ loads, as JSON: {"name": "Insider"} or {"line": 3}. ESQ
/// loads it when it shows the loaded logo, so send this request before the logo command. The name is the path of the
/// line, its file name, or its file name without the extension. Upper case and lower case are the same.
/// </item>
/// </list>
/// </remarks>
public sealed class LogoRequests(Core core, PrevueState state, EsqVariables esq) : IStreamRequests
{
    public string Prefix => "prevue/logos";

    public void Answer(HttpListenerContext context, string name)
    {
        var response = context.Response;
        try
        {
            switch (context.Request.HttpMethod, name.TrimEnd('/'))
            {
                case ("GET", "prevue/logos"):
                    break;
                case ("POST", "prevue/logos/next"):
                {
                    using var document = ReadJson(context);
                    state.SetNextLine(FindLine(document.RootElement));
                    break;
                }
                default:
                    SendError(response, 404, "Use GET /prevue/logos or POST /prevue/logos/next.");
                    return;
            }

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                state.WriteLogos(json);
                json.WriteEndObject();
            }

            Send(response, "application/json", buffer.ToArray());
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            SendError(response, 400, e.Message);
        }
    }

    /// <summary>Finds the line of a request: {"line": 3} or {"name": "Insider"}.</summary>
    /// <exception cref="FormatException">The request has no line of the list.</exception>
    private int FindLine(JsonElement request)
    {
        var lines = PrevueState.LogoList(core.Memory, esq);
        if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("line", out var number) &&
            number.ValueKind == JsonValueKind.Number)
        {
            var line = number.GetInt32();
            return lines.Any(entry => entry.Line == line)
                ? line
                : throw new FormatException($"LOGO.LST has no logo on line {line}.");
        }

        var name = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("name", out var text) &&
                   text.ValueKind == JsonValueKind.String
            ? text.GetString()!
            : throw new FormatException("Give \"name\" or \"line\", for example {\"name\": \"Insider\"}.");
        foreach (var entry in lines)
        {
            var file = entry.Path[(entry.Path.LastIndexOfAny(['/', ':']) + 1)..];
            if (Matches(entry.Path) || Matches(file) || Matches(Path.GetFileNameWithoutExtension(file)))
                return entry.Line;
        }

        var names = new StringBuilder();
        foreach (var entry in lines)
            names.Append(names.Length > 0 ? ", " : "").Append(entry.Path);
        throw new FormatException($"LOGO.LST has no logo {name}. The logos are: {names}.");

        bool Matches(string candidate) => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase);
    }
}
