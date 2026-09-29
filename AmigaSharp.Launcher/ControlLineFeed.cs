using System.Text.Json;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Launcher;

/// <summary>
/// The bytes of the control line of Prevue (see docs/ctrl-line.md). The HTTP server adds packets, for example the
/// packets of a promo. The line also reads the bytes of another source, the TCP bridge or a file.
/// </summary>
/// <remarks>
/// The line sends each packet of the HTTP server without a gap. It reads the other source only between these packets.
/// Do not use the two sources at the same time, because a packet of one source can come in a packet of the other.
/// </remarks>
public sealed class ControlLineFeed(ISerialConnection? other = null) : ISerialConnection
{
    /// <summary>The bytes that the line sends in one second: 110 baud, and 10 bits for each byte.</summary>
    public const double BytesPerSecond = 11;

    // Prevue counts a packet as too long after 198 bytes of type, body and CR.
    private const int MaximumBody = 196;
    private const byte PacketEnd = 0x0D;
    private const char Separator = '\x12';

    private readonly object _lock = new();
    private readonly Queue<byte> _queue = new();

    /// <summary>The bytes of the HTTP server that wait for the line.</summary>
    public int Queued
    {
        get
        {
            lock (_lock)
                return _queue.Count;
        }
    }

    /// <summary>The bytes that the line took, from all the sources.</summary>
    public long Sent { get; private set; }

    public int Available => Queued + (other?.Available ?? 0);

    /// <summary>Adds packets to the end of the queue. The line sends them in sequence, without a gap.</summary>
    public void Add(IEnumerable<byte[]> packets)
    {
        lock (_lock)
        {
            foreach (var packet in packets)
            {
                foreach (var value in packet)
                    _queue.Enqueue(value);
            }
        }
    }

    public bool TryRead(out byte value)
    {
        bool read;
        lock (_lock)
            read = _queue.TryDequeue(out value);
        if (!read && other != null)
            read = other.TryRead(out value);
        if (read)
            Sent++;
        return read;
    }

    public void Write(byte value)
    {
    }

    /// <summary>Makes a packet: the type, the body, a CR, and the XOR of all these bytes.</summary>
    /// <exception cref="FormatException">The type or the body is not correct.</exception>
    public static byte[] Packet(int type, string body)
    {
        if (type is < 1 or > 22)
            throw new FormatException("The type must be from 1 to 22.");
        if (body.Length > MaximumBody)
            throw new FormatException($"The body must have {MaximumBody} characters or less.");
        var bytes = new byte[body.Length + 3];
        bytes[0] = (byte)type;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] > 0xFF || body[i] == PacketEnd)
                throw new FormatException("The body must have Latin-1 characters only, and no CR.");
            bytes[i + 1] = (byte)body[i];
        }

        bytes[^2] = PacketEnd;
        byte checksum = 0;
        foreach (var value in bytes.AsSpan(0, bytes.Length - 1))
            checksum ^= value;
        bytes[^1] = checksum;
        return bytes;
    }

    /// <summary>
    /// Makes the packets of a promo request. The request has a "right" box and a "left" box, and at least one of them.
    /// Each box has a "title" to find, and optional "channels" (a pattern of call letters, "*" by default) and "brush"
    /// (the ID of its background in BRUSH.INI). "title", "channels" and "brush" at the top are for the right box.
    /// "first" is "right" (the default) or "left": the box that Prevue tries first.
    /// </summary>
    /// <exception cref="FormatException">The request is not correct.</exception>
    public static List<byte[]> Promo(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
            throw new FormatException("A promo must be a JSON object, for example {\"title\": \"Seinfeld\"}.");
        var right = request.TryGetProperty("right", out var element) ? ReadBox(element, "right") : null;
        if (request.TryGetProperty("title", out _))
        {
            if (right != null)
                throw new FormatException("Give \"title\" or \"right\", not the two.");
            right = ReadBox(request, "right");
        }

        var left = request.TryGetProperty("left", out element) ? ReadBox(element, "left") : null;
        if (right == null && left == null)
            throw new FormatException("A promo needs \"title\", \"right\" or \"left\".");
        var first = request.TryGetProperty("first", out element) ? element.GetString() : "right";
        if (first is not ("right" or "left"))
            throw new FormatException("\"first\" must be \"right\" or \"left\".");

        var packets = new List<byte[]>();
        if (right?.Brush != null || left?.Brush != null)
            packets.Add(Packet(2, (right?.Brush ?? "00") + (left?.Brush ?? "00")));
        if (right != null && left != null)
            packets.Add(Packet(4, first == "right" ? "L" : "R"));
        packets.Add(Packet(17, right?.Title + (left == null ? "" : Separator + left.Title)));
        // Prevue needs a pattern before the separator, also for the left box only.
        packets.Add(Packet(1, "1" + (right?.Channels ?? "*") + (left == null ? "" : Separator + left.Channels)));
        return packets;
    }

    /// <summary>
    /// Makes the packets of a list of raw packets, for example [{"type": 1, "body": "3"}]. The body of a packet is
    /// text, and "\u0012" is the separator of Prevue.
    /// </summary>
    /// <exception cref="FormatException">The request is not correct.</exception>
    public static List<byte[]> Packets(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Array)
            throw new FormatException("The packets must be a JSON array, for example [{\"type\": 1, \"body\": \"3\"}].");
        var packets = new List<byte[]>();
        foreach (var element in request.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number)
                throw new FormatException("Each packet needs a \"type\" from 1 to 22.");
            var body = element.TryGetProperty("body", out var value) ? value.GetString() ?? "" : "";
            packets.Add(Packet(type.GetInt32(), body));
        }

        return packets;
    }

    private sealed record Box(string Title, string Channels, string? Brush);

    private static Box ReadBox(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException($"\"{name}\" must be a JSON object, for example {{\"title\": \"Seinfeld\"}}.");
        var title = ReadText(element, "title") ?? throw new FormatException($"The {name} box needs a \"title\".");
        var channels = ReadText(element, "channels") ?? "*";
        var brush = ReadText(element, "brush");
        if (brush is { Length: not 2 })
            throw new FormatException("A brush is an ID of 2 characters, for example \"AT\".");
        return new Box(title, channels, brush);
    }

    /// <summary>
    /// Reads an optional text of a request. The text must not be empty or have control characters. Typographic quotes
    /// and dashes become ASCII characters, as in the listings of Prevue.
    /// </summary>
    private static string? ReadText(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        if (text.Length == 0 || text.Any(char.IsControl))
            throw new FormatException($"\"{name}\" must be text, without control characters.");
        return text.Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201C', '"').Replace('\u201D', '"')
            .Replace('\u2013', '-').Replace('\u2014', '-');
    }
}
