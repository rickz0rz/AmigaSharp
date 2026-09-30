using System.Net;
using System.Text;
using System.Text.Json;
using AmigaSharp.Host;
using AmigaSharp.Runtime;
using static AmigaSharp.Host.HttpJson;

namespace AmigaSharp.PrevueLauncher;

/// <summary>
/// The request GET /prevue/guide: the programs of the listings of ESQ from now, for example to see what an automatic
/// promo can choose. The query "hours" is the hours after now (the default is 3), and "now=true" also gives the
/// programs that play now.
/// </summary>
public sealed class PrevueGuideRequests(Memory memory, EsqVariables esq) : IStreamRequests
{
    public string Prefix => "prevue/guide";

    public void Answer(HttpListenerContext context, string name)
    {
        if (context.Request.HttpMethod != "GET" || name.TrimEnd('/') != Prefix)
        {
            SendError(context.Response, 404, "Use GET /prevue/guide.");
            return;
        }

        var query = context.Request.QueryString;
        var hours = double.TryParse(query["hours"], System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0.5, 24)
            : 3;
        var criteria = new AutoCriteria(null, null, [], [], hours, query["now"] == "true", 0);
        var current = PrevueGuide.CurrentSlot(memory, esq);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("slot", current);
            json.WriteStartArray("programs");
            foreach (var program in PrevueGuide.Read(memory, esq).Where(program => criteria.Matches(program, current))
                         .OrderBy(program => program.Slot).ThenBy(program => program.Channel))
                WriteProgram(json, program, current);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        Send(context.Response, "application/json", buffer.ToArray());
    }

    /// <summary>
    /// Writes a program as a JSON object. "minutes" is the time from the start of the current half hour to the start of
    /// the program's half hour.
    /// </summary>
    public static void WriteProgram(Utf8JsonWriter json, GuideProgram program, int currentSlot)
    {
        json.WriteStartObject();
        json.WriteNumber("slot", program.Slot);
        json.WriteNumber("minutes", program.MinutesFrom(currentSlot));
        json.WriteString("channel", program.Number);
        json.WriteString("callLetters", program.CallLetters);
        json.WriteString("title", program.Title);
        json.WriteBoolean("movie", program.Movie);
        json.WriteBoolean("premium", program.Premium);
        json.WriteEndObject();
    }
}
