using AmigaSharp.Runtime;
using AmigaSharp.Translator;

namespace AmigaSharp.Launcher.Prevue;

/// <summary>
/// Reports what the feed parser of ESQ does with the serial bytes. The trace reads the variables of ESQ at each safe
/// point. It finds their addresses with the labels of the listing, so the translated code does not change.
/// </summary>
/// <remarks>
/// The RBF interrupt of ESQ puts each byte in a ring buffer. The parser reads the bytes at the tail. The trace reads
/// the bytes that the parser took and finds the commands: $55, $AA and a command byte. Then it reports each change of
/// the counters. A change after a command shows if the parser accepted the record or rejected it. For example, a
/// change of _DATACErrs is a checksum error.
/// </remarks>
public sealed class FeedTrace
{
    private const uint RingBufferSize = 0xFA00;

    /// <summary>The variables that the trace reports, with their sizes in bytes. The selection code must be first.</summary>
    private static readonly (string Label, int Size, string Meaning)[] Watched =
    [
        ("_ESQPARS_SelectionMatchCode", 2, "1: the selection code matches, and the parser accepts commands"),
        ("_ESQIFF_ParseAttemptCount", 2, "a record started"),
        ("_DATACErrs", 2, "a checksum error"),
        ("_ESQIFF_LineErrorCount", 2, "a record that is too long, or a line error"),
        ("_ESQ_SerialRbfErrorCount", 2, "a received byte with an overrun"),
        ("_SCRIPT_SerialReadModeOverflowCount", 4, "the ring buffer is almost full"),
        ("_ESQPARS2_ReadModeFlags", 2, "the read mode. $102: the ring buffer is almost full"),
        ("_ESQPARS_ResetArmedFlag", 2, "1: a reset is armed"),
    ];

    private static readonly Dictionary<byte, string> Commands = new()
    {
        [(byte)'!'] = "block start", [(byte)'%'] = "block start", [(byte)'='] = "binary download", [(byte)'A'] = "selection code",
        [(byte)'C'] = "group record", [(byte)'D'] = "diagnostics", [(byte)'E'] = "copy string", [(byte)'F'] = "status packet",
        [(byte)'H'] = "binary download", [(byte)'I'] = "digit label", [(byte)'K'] = "clock", [(byte)'L'] = "banner entry",
        [(byte)'M'] = "acknowledge", [(byte)'O'] = "clear primary flags", [(byte)'P'] = "compact entry", [(byte)'R'] = "reset",
        [(byte)'V'] = "version", [(byte)'W'] = "verify record", [(byte)'c'] = "program information", [(byte)'f'] = "configuration record",
        [(byte)'g'] = "filter or banner", [(byte)'h'] = "binary download", [(byte)'i'] = "copy label", [(byte)'j'] = "line head and tail",
        [(byte)'p'] = "p record", [(byte)'t'] = "banner entry", [(byte)'v'] = "aligned listing", [(byte)'w'] = "verify list",
        [(byte)'x'] = "font command", [0xBB] = "box off",
    };

    private readonly Core _core;
    private readonly TextWriter _log;
    private readonly uint _bufferPointer;
    private readonly uint _tail;
    private readonly (string Label, uint Address, int Size, string Meaning)[] _watched;
    private readonly uint[] _values;
    private uint _lastTail;
    private long _consumed;
    private int _preamble;

    private FeedTrace(Core core, TextWriter log, uint bufferPointer, uint tail,
        (string, uint, int, string)[] watched)
    {
        _core = core;
        _log = log;
        _bufferPointer = bufferPointer;
        _tail = tail;
        _watched = watched;
        _values = new uint[watched.Length];
    }

    /// <summary>
    /// Makes a trace for the program. Returns null, with a message in the log, if the listing does not have the labels
    /// of the ESQ parser.
    /// </summary>
    public static FeedTrace? Create(Core core, VasmListing listing, uint[] bases, TextWriter log)
    {
        uint? Address(string label) =>
            listing.Symbols.TryGetValue(label, out var symbol) && symbol.Section is { } section && section < bases.Length
                ? bases[section] + symbol.Value
                : null;

        var missing = new List<string>();
        uint Required(string label)
        {
            var address = Address(label);
            if (address == null)
                missing.Add(label);
            return address ?? 0;
        }

        var bufferPointer = Required("_Global_REF_INTB_RBF_64K_BUFFER");
        var tail = Required("_Global_WORD_T_VALUE");
        var watched = Watched.Select(variable => (variable.Label, Required(variable.Label), variable.Size, variable.Meaning)).ToArray();
        if (missing.Count > 0)
        {
            log.WriteLine($"The feed trace is off. The listing does not have {string.Join(", ", missing)}.");
            return null;
        }

        var trace = new FeedTrace(core, log, bufferPointer, tail, watched);
        for (var i = 0; i < watched.Length; i++)
            trace._values[i] = trace.Read(watched[i].Item2, watched[i].Item3);
        core.AddPollHandler(trace.Poll);
        return trace;
    }

    private void Poll()
    {
        var memory = _core.Memory;
        var buffer = memory.Read32(_bufferPointer);
        var tail = (uint)memory.Read16(_tail);
        if (buffer != 0 && tail != _lastTail)
        {
            for (var position = _lastTail; position != tail; position = (position + 1) % RingBufferSize)
                Consume(memory.Read8(buffer + position));
            _lastTail = tail;
        }

        for (var i = 0; i < _watched.Length; i++)
        {
            var (label, address, size, meaning) = _watched[i];
            var value = Read(address, size);
            if (value == _values[i])
                continue;
            Write($"{label} {_values[i]} -> {value} ({meaning})");
            _values[i] = value;
        }
    }

    /// <summary>Follows the preamble as the dispatcher does: $55, then $AA, then the command byte.</summary>
    private void Consume(byte value)
    {
        _consumed++;
        switch (_preamble)
        {
            case 2:
                var name = Commands.TryGetValue(value, out var known) ? known : "unknown command";
                var shown = value is >= 0x20 and < 0x7F ? $"'{(char)value}'" : $"${value:X2}";
                // Until the selection code matches, the dispatcher reads only A, W and w.
                var selected = _core.Memory.Read16(_watched[0].Address) == 1;
                var ignored = !selected && value is not ((byte)'A' or (byte)'W' or (byte)'w');
                Write($"command {shown}: {name}{(ignored ? " (ignored: the selection code does not match)" : "")}");
                _preamble = 0;
                break;
            case 1 when value == 0xAA:
                _preamble = 2;
                break;
            default:
                _preamble = value == 0x55 ? 1 : 0;
                break;
        }
    }

    private void Write(string text) =>
        _log.WriteLine($"{_core.Chipset.Beam.Clock.Elapsed.TotalSeconds,12:F6}  byte {_consumed,8}  {text}");

    private uint Read(uint address, int size) => size switch
    {
        1 => _core.Memory.Read8(address),
        2 => _core.Memory.Read16(address),
        _ => _core.Memory.Read32(address),
    };
}
