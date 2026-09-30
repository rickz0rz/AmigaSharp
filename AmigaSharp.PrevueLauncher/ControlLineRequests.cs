using System.Net;
using System.Text.Json;
using AmigaSharp.Host;
using static AmigaSharp.Host.HttpJson;

namespace AmigaSharp.PrevueLauncher;

/// <summary>The requests of /prevue/ctrl on the port of the stream: they send commands on the control line.</summary>
public sealed class ControlLineRequests(ControlLineFeed line) : IStreamRequests
{
    public string Prefix => "prevue/ctrl";

    /// <summary>The last request that sent commands, for example "promo". Null before the first one.</summary>
    public string? LastRequest { get; private set; }

    /// <summary>Sends packets on the line as a request, for example of a schedule. The state notes the request.</summary>
    public void SendRequest(string request, IEnumerable<byte[]> packets)
    {
        line.Add(packets);
        LastRequest = request;
    }

    /// <summary>Answers a request for the control line of Prevue (see docs/ctrl-line.md).</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// GET /prevue/ctrl: the bytes that wait for the line, and the seconds that the line needs to send them.
    /// </item>
    /// <item>
    /// POST /prevue/ctrl/promo: shows a promo, as JSON: {"title": "Seinfeld", "channels": "*", "brush": "AT"}, or
    /// {"right": {...}, "left": {...}, "first": "right"} (see <see cref="ControlLineFeed.Promo"/>).
    /// </item>
    /// <item>
    /// POST /prevue/ctrl/clear: removes the promo or the logo, so that the genlock video shows in the top half.
    /// </item>
    /// <item>POST /prevue/ctrl/logo: shows the current logo in the top half.</item>
    /// <item>POST /prevue/ctrl/packets: sends raw packets, as JSON: [{"type": 1, "body": "3"}].</item>
    /// </list>
    /// The line sends 11 bytes each second, so a promo takes about 2 seconds. Each answer is the answer of
    /// GET /prevue/ctrl.
    /// </remarks>
    public void Answer(HttpListenerContext context, string name)
    {
        var response = context.Response;
        try
        {
            switch (context.Request.HttpMethod, name.TrimEnd('/'))
            {
                case ("GET", "prevue/ctrl"):
                    break;
                case ("POST", "prevue/ctrl/promo"):
                {
                    using var document = ReadJson(context);
                    line.Add(ControlLineFeed.Promo(document.RootElement));
                    LastRequest = "promo";
                    break;
                }
                case ("POST", "prevue/ctrl/clear"):
                    line.Add([ControlLineFeed.Packet(1, "3")]);
                    LastRequest = "clear";
                    break;
                case ("POST", "prevue/ctrl/logo"):
                    line.Add([ControlLineFeed.Packet(1, "D")]);
                    LastRequest = "logo";
                    break;
                case ("POST", "prevue/ctrl/packets"):
                {
                    using var document = ReadJson(context);
                    line.Add(ControlLineFeed.Packets(document.RootElement));
                    LastRequest = "packets";
                    break;
                }
                default:
                    SendError(response, 404, "Use GET /prevue/ctrl, POST /prevue/ctrl/promo, " +
                                             "POST /prevue/ctrl/clear, POST /prevue/ctrl/logo or " +
                                             "POST /prevue/ctrl/packets.");
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
}
