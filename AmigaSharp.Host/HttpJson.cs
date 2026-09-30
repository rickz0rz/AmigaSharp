using System.Net;
using System.Text;
using System.Text.Json;

namespace AmigaSharp.Host;

/// <summary>
/// Answers a group of requests on the port of the stream, for example the requests of /prevue/ctrl. The stream gives
/// each request whose path is <see cref="Prefix"/>, or starts with the prefix and a "/", to <see cref="Answer"/>.
/// </summary>
public interface IStreamRequests
{
    /// <summary>The path of the requests without the first "/", for example "prevue/ctrl".</summary>
    string Prefix { get; }

    /// <param name="name">The path of the request without the first "/".</param>
    void Answer(HttpListenerContext context, string name);
}

/// <summary>Reads the JSON of a request, and sends the answer.</summary>
public static class HttpJson
{
    /// <summary>Reads the body of a request as JSON. An empty body is an empty object.</summary>
    public static JsonDocument ReadJson(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    /// <summary>Reads an optional number of a request, in its range.</summary>
    /// <exception cref="FormatException">The number is not a number or is out of its range.</exception>
    public static double ReadNumber(JsonElement root, string name, double current, double minimum, double maximum)
    {
        if (!root.TryGetProperty(name, out var value))
            return current;
        var number = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN;
        if (double.IsNaN(number) || number < minimum || number > maximum)
            throw new FormatException($"\"{name}\" must be a number from {minimum} to {maximum}.");
        return number;
    }

    /// <summary>Sends an error as JSON: {"error": "..."}.</summary>
    public static void SendError(HttpListenerResponse response, int status, string message)
    {
        response.StatusCode = status;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("error", message);
            json.WriteEndObject();
        }

        Send(response, "application/json", buffer.ToArray());
    }

    /// <summary>Sends the answer, and closes the response.</summary>
    public static void Send(HttpListenerResponse response, string type, byte[] body)
    {
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
        response.Close();
    }
}
