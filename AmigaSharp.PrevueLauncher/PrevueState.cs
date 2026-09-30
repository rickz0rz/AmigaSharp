using System.Net;
using System.Text;
using System.Text.Json;
using AmigaSharp.Host;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;
using static AmigaSharp.Host.HttpJson;

namespace AmigaSharp.PrevueLauncher;

/// <summary>
/// The state of Prevue for a coordinator: what the top half of the screen shows, if Prevue read the commands of the
/// control line, and the logos. The request is GET /prevue/state.
/// </summary>
/// <remarks>
/// The top half comes from the picture of the display: the genlock key (color 0) is transparent, and the video of the
/// genlock shows there. A promo covers one half of the top of the screen, and a logo covers all of it. The other
/// values come from variables of ESQ (see <see cref="EsqVariables"/>). Without them, the state has the top half and
/// the queue of the launcher only.
/// </remarks>
public sealed class PrevueState : IStreamRequests
{
    /// <summary>The size of the ring buffer of ESQ for the bytes of the control line.</summary>
    private const int BufferSize = 500;

    // The areas of the top half that the state tests. A promo on the right covers the right area only, a promo on the
    // left covers the left area only, and a logo covers the two.
    private const int Top = 20, Bottom = 220;
    private const int LeftStart = 100, LeftEnd = 380, RightStart = 420, RightEnd = 720;

    private readonly Core _core;
    private readonly ControlLineFeed _line;
    private readonly ControlLineRequests _requests;
    private readonly EsqVariables? _esq;
    private readonly TextWriter? _log;
    private readonly Func<DateTime> _now;

    // The logo that ESQ loaded, and the last logo that it showed. ESQ shows the loaded logo, and then it loads the
    // next one, so a change of the loaded logo means that the old one showed.
    private uint _loadedNode;
    private volatile string? _loadedName;
    private volatile string? _shownName;

    // A logo to load in place of the loaded logo (a line of LOGO.LST and its path), and a logo to show when it is
    // loaded.
    private readonly object _swapLock = new();
    private int? _swapLine;
    private string? _swapPath;
    private string? _showName;
    private int? _nextAfterShow;

    // Logos that the launcher took from ESQ. It frees their memory some seconds later, when ESQ cannot use them.
    private readonly List<(uint Node, DateTime Time)> _removed = [];

    // The layout of a brush node of ESQ (see BRUSH_FreeBrushList).
    private const int NodeRasters = 0x90, NodeWidth = 176, NodeHeight = 178, NodeRasterCount = 184;
    private const int NodeAuxList = 364, NodeNext = 368, NodeSize = 372, AuxNext = 8, AuxSize = 12;

    // A logo command draws the logo for about a second. The launcher changes the loaded logo only when no command came
    // for this time, so that ESQ does not draw it then.
    private static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FreeDelay = TimeSpan.FromSeconds(10);

    /// <param name="now">The time for the waits of the logo changes. The default is the clock of the host.</param>
    public PrevueState(Core core, ControlLineFeed line, ControlLineRequests requests, EsqVariables? esq,
        TextWriter? log = null, Func<DateTime>? now = null)
    {
        _log = log;
        _now = now ?? (() => DateTime.UtcNow);
        _core = core;
        _line = line;
        _requests = requests;
        _esq = esq;
        if (esq != null)
            core.AddPollHandler(Poll);
    }

    public string Prefix => "prevue/state";

    /// <summary>The name of the last logo that ESQ showed, or null if it showed none yet.</summary>
    public string? Shown => _shownName;

    /// <summary>The path of the logo that the launcher shows when ESQ loaded it, or null.</summary>
    public string? PendingLogo
    {
        get
        {
            lock (_swapLock)
                return _showName;
        }
    }

    /// <summary>
    /// Shows a line of LOGO.LST now, or as soon as ESQ loaded it. If ESQ loaded another logo, the launcher removes it
    /// from ESQ, and ESQ loads this line. The other logo does not show.
    /// </summary>
    /// <exception cref="FormatException">LOGO.LST does not have the line.</exception>
    /// <param name="nextLine">The line that ESQ loads after the logo shows, or null for the line after it.</param>
    public void ShowLogo(int line, int? nextLine = null)
    {
        LoadLogo(line);
        lock (_swapLock)
        {
            _showName = _swapPath;
            _nextAfterShow = nextLine;
        }
    }

    /// <summary>
    /// Makes a line of LOGO.LST the loaded logo, and shows nothing. If ESQ loaded another logo, the launcher removes it
    /// from ESQ, and ESQ loads this line. The next logo command shows it.
    /// </summary>
    /// <exception cref="FormatException">LOGO.LST does not have the line.</exception>
    public void LoadLogo(int line)
    {
        var entry = LogoList(_core.Memory, _esq!).FirstOrDefault(candidate => candidate.Line == line);
        if (entry.Path == null)
            throw new FormatException($"LOGO.LST has no logo on line {line}.");
        lock (_swapLock)
        {
            _swapLine = line;
            _swapPath = entry.Path;
        }
    }

    /// <summary>
    /// Runs at each safe point of the program, between two instructions of ESQ. It notes the loaded logo and the logo
    /// that showed, changes the loaded logo for <see cref="ShowLogo"/>, and frees the logos that it removed.
    /// </summary>
    private void Poll()
    {
        var memory = _core.Memory;
        var esq = _esq!;
        var node = memory.Read32(esq[EsqVariables.LoadedLogo]);
        if (node != _loadedNode)
        {
            if (_loadedNode != 0)
                _shownName = _loadedName;
            _loadedNode = node;
            _loadedName = node == 0 ? null : ReadString(memory, node, 190);
        }

        // ESQ counts its loaded logos. If it drew a logo when the launcher removed it, the count is one too low.
        if ((int)memory.Read32(esq[EsqVariables.LoadedLogoCount]) < 0)
            memory.Write32(esq[EsqVariables.LoadedLogoCount], 0);

        lock (_swapLock)
        {
            if (_swapLine is { } line && node != 0)
            {
                if (_loadedName != null && IsLogo(_swapPath!, _loadedName))
                    _swapLine = null;
                else if (Quiet())
                    RemoveLoadedLogo(node, line);
            }

            // The queue of the line keeps the order of the commands, so the logo command can go at once.
            if (_showName is { } show && _loadedName != null && IsLogo(show, _loadedName) && _swapLine == null)
            {
                _showName = null;
                // ESQ loads the next line when it shows the logo, after it reads the command.
                if (_nextAfterShow is { } next)
                    SetNextLine(next);
                _nextAfterShow = null;
                _requests.SendRequest("logo", [ControlLineFeed.Packet(1, "D")]);
            }
        }

        for (var i = _removed.Count - 1; i >= 0; i--)
        {
            if (_now() - _removed[i].Time < FreeDelay)
                continue;
            FreeLogo(_removed[i].Node);
            _removed.RemoveAt(i);
        }
    }

    /// <summary>True if ESQ read all the commands, and no command came for <see cref="QuietTime"/>.</summary>
    private bool Quiet()
    {
        var memory = _core.Memory;
        var esq = _esq!;
        var inEsq = (memory.Read16(esq[EsqVariables.BufferHead]) - memory.Read16(esq[EsqVariables.BufferTail]) +
                     BufferSize) % BufferSize;
        return _line.Queued == 0 && inEsq == 0 && memory.Read16(esq[EsqVariables.ParserState]) == 0 &&
               _now() - _requests.LastSent >= QuietTime;
    }

    /// <summary>Takes the loaded logo from ESQ, so that ESQ loads the line. The launcher frees the logo later.</summary>
    private void RemoveLoadedLogo(uint node, int line)
    {
        var memory = _core.Memory;
        var esq = _esq!;
        memory.Write32(esq[EsqVariables.LoadedLogo], memory.Read32(node + NodeNext));
        memory.Write32(esq[EsqVariables.LoadedLogoCount], memory.Read32(esq[EsqVariables.LoadedLogoCount]) - 1);
        SetNextLine(line);
        _removed.Add((node, _now()));
        _log?.WriteLine($"Prevue: ESQ loads {_swapPath} in place of {_loadedName}.");
        // The logo did not show, so it is not the shown logo.
        _loadedNode = memory.Read32(esq[EsqVariables.LoadedLogo]);
        _loadedName = _loadedNode == 0 ? null : ReadString(memory, _loadedNode, 190);
        _swapLine = null;
    }

    /// <summary>Frees a brush node of ESQ, its rasters and its other nodes, as BRUSH_FreeBrushList of ESQ does.</summary>
    private void FreeLogo(uint node)
    {
        var memory = _core.Memory;
        var esq = _esq!;
        var width = memory.Read16(node + NodeWidth);
        var height = memory.Read16(node + NodeHeight);
        int count = memory.Read8(node + NodeRasterCount), frees = 1;
        uint bytes = NodeSize;
        for (var i = 0u; i < count && i < 8; i++)
        {
            var raster = memory.Read32(node + NodeRasters + 4 * i);
            if (raster != 0)
                _core.Allocator.Free(raster, AmigaSharp.Runtime.Libraries.Native.GraphicsLibrary.RasterSize(width, height));
        }

        var aux = memory.Read32(node + NodeAuxList);
        while (aux != 0)
        {
            var next = memory.Read32(aux + AuxNext);
            _core.Allocator.Free(aux, AuxSize);
            bytes += AuxSize;
            frees++;
            aux = next;
        }

        _core.Allocator.Free(node, NodeSize);
        // ESQ counts its memory for its status screen.
        memory.Write32(esq[EsqVariables.BytesAllocated], memory.Read32(esq[EsqVariables.BytesAllocated]) - bytes);
        memory.Write32(esq[EsqVariables.FreeCount], memory.Read32(esq[EsqVariables.FreeCount]) + (uint)frees);
    }

    public void Answer(HttpListenerContext context, string name)
    {
        if (context.Request.HttpMethod != "GET" || name.TrimEnd('/') != Prefix)
        {
            SendError(context.Response, 404, "Use GET /prevue/state.");
            return;
        }

        Send(context.Response, "application/json", Encoding.UTF8.GetBytes(ToJson()));
    }

    /// <summary>
    /// Tells what the top half shows: "video" (the genlock key), "promo-right", "promo-left", "logo", or "other".
    /// </summary>
    public static string ClassifyTopHalf(uint[] pixels)
    {
        var left = TransparentPart(pixels, LeftStart, LeftEnd);
        var right = TransparentPart(pixels, RightStart, RightEnd);
        return (left, right) switch
        {
            ( > 0.8, > 0.8) => "video",
            ( > 0.8, < 0.2) => "promo-right",
            ( < 0.2, > 0.8) => "promo-left",
            ( < 0.2, < 0.2) => "logo",
            _ => "other",
        };
    }

    /// <summary>The state as JSON.</summary>
    public string ToJson()
    {
        var pixels = new uint[Display.Width * Display.Height];
        _core.Chipset.Display.CopyFrame(pixels);
        var memory = _core.Memory;

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("topHalf", ClassifyTopHalf(pixels));

            json.WriteStartObject("line");
            json.WriteNumber("queued", _line.Queued);
            bool? idle = null;
            if (_esq != null)
            {
                var inEsq = (memory.Read16(_esq[EsqVariables.BufferHead]) -
                             memory.Read16(_esq[EsqVariables.BufferTail]) + BufferSize) % BufferSize;
                idle = _line.Queued == 0 && inEsq == 0 && memory.Read16(_esq[EsqVariables.ParserState]) == 0;
                json.WriteNumber("inEsq", inEsq);
                json.WriteBoolean("idle", idle.Value);
                json.WriteNumber("commands", memory.Read16(_esq[EsqVariables.CommandCount]));
                json.WriteNumber("checksumErrors", memory.Read16(_esq[EsqVariables.ChecksumErrorCount]));
            }

            json.WriteEndObject();

            json.WritePropertyName("lastRequest");
            if (_requests.LastRequest is { } last)
            {
                json.WriteStartObject();
                json.WriteString("name", last);
                if (idle is { } read)
                    json.WriteBoolean("read", read);
                json.WriteEndObject();
            }
            else
            {
                json.WriteNullValue();
            }

            if (_esq != null)
                WriteLogos(json);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The part of an area of the top half that is transparent (alpha 0), from 0 to 1.</summary>
    private static double TransparentPart(uint[] pixels, int start, int end)
    {
        int transparent = 0, count = 0;
        for (var y = Top; y < Bottom; y += 4)
        {
            for (var x = start; x < end; x += 4)
            {
                count++;
                if (pixels[y * Display.Width + x] >> 24 == 0)
                    transparent++;
            }
        }

        return (double)transparent / count;
    }

    /// <summary>
    /// Writes the logos: the lines of LOGO.LST as ESQ read them, the logo that ESQ loaded (the next logo that shows), the
    /// line that ESQ loads after it, and the last logo that showed.
    /// </summary>
    public void WriteLogos(Utf8JsonWriter json)
    {
        var memory = _core.Memory;
        var esq = _esq!;
        var lines = LogoList(memory, esq);
        json.WriteStartObject("logos");
        var loaded = memory.Read32(esq[EsqVariables.LoadedLogo]);
        if (loaded != 0)
            json.WriteString("loaded", ReadString(memory, loaded, 190));
        else
            json.WriteNull("loaded");
        // ESQ counts the lines of the file that it read. It reads the next line, or the first line after the last.
        var read = memory.Read16(esq[EsqVariables.LogoListLine]);
        var next = lines.FirstOrDefault(entry => entry.Line > read);
        json.WriteNumber("nextLine", next.Path != null ? next.Line : lines.Count > 0 ? lines[0].Line : 0);
        if (_shownName != null)
            json.WriteString("shown", _shownName);
        else
            json.WriteNull("shown");
        if (PendingLogo is { } pending)
            json.WriteString("pending", pending);
        else
            json.WriteNull("pending");
        json.WriteStartArray("list");
        foreach (var (number, path, channel) in lines)
        {
            json.WriteStartObject();
            json.WriteNumber("line", number);
            json.WriteString("path", path);
            json.WriteBoolean("channel", channel);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>The file name of the logo that ESQ loaded, or null if it loads a logo now or has none.</summary>
    public string? LoadedLogo
    {
        get
        {
            var node = _core.Memory.Read32(_esq![EsqVariables.LoadedLogo]);
            return node == 0 ? null : ReadString(_core.Memory, node, 190);
        }
    }

    /// <summary>
    /// True if the name is the logo: its path, its file name, or its file name without the extension. Upper case and
    /// lower case are the same.
    /// </summary>
    public static bool IsLogo(string path, string name)
    {
        var file = path[(path.LastIndexOfAny(['/', ':']) + 1)..];
        return string.Equals(path, name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(file, name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The line of LOGO.LST of a logo name, or null if the list does not have it.</summary>
    public int? FindLogoLine(string name)
    {
        foreach (var entry in LogoList(_core.Memory, _esq!))
        {
            if (IsLogo(entry.Path, name))
                return entry.Line;
        }

        return null;
    }

    /// <summary>
    /// Makes a line of LOGO.LST the next line that ESQ loads. ESQ loads it when it shows the loaded logo, so the change
    /// is for the next show of a logo.
    /// </summary>
    public void SetNextLine(int line) => _core.Memory.Write16(_esq![EsqVariables.LogoListLine], (ushort)(line - 1));

    /// <summary>
    /// The lines of LOGO.LST in the memory of ESQ, with their numbers in the file (from 1). A line without a comma is a
    /// channel logo. The list does not have the empty lines.
    /// </summary>
    public static List<(int Line, string Path, bool Channel)> LogoList(Memory memory, EsqVariables esq)
    {
        var data = memory.Read32(esq[EsqVariables.LogoListData]);
        var size = memory.Read32(esq[EsqVariables.LogoListSize]);
        var result = new List<(int, string, bool)>();
        if (data == 0 || size is 0 or > 0x10000)
            return result;
        var bytes = new byte[size];
        for (var i = 0u; i < size; i++)
            bytes[i] = memory.Read8(data + i);
        var lines = Encoding.Latin1.GetString(bytes).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var path = lines[i].Trim('\r', ' ', '\0');
            if (path.Length == 0)
                continue;
            var comma = path.IndexOf(',');
            result.Add(comma >= 0 ? (i + 1, path[..comma], false) : (i + 1, path, true));
        }

        return result;
    }

    private static string ReadString(Memory memory, uint address, int maximum)
    {
        var text = new StringBuilder();
        for (var i = 0; i < maximum; i++)
        {
            var value = memory.Read8(address + (uint)i);
            if (value == 0)
                break;
            text.Append((char)value);
        }

        return text.ToString();
    }
}
