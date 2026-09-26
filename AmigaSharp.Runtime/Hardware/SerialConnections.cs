using System.Text;

namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// Sends the bytes of a captured feed to the serial port. The serial port sets the rate: it takes the next byte only
/// when the program read the last byte and the time of one byte at the baud rate has passed. With a virtual clock, a
/// replay is the same each time. The bytes that the Amiga sends are lost.
/// </summary>
public sealed class ReplaySerialConnection : ISerialConnection
{
    private readonly byte[] _data;
    private readonly Func<bool> _canStart;
    private bool _started;

    /// <param name="data">The bytes of the feed.</param>
    /// <param name="canStart">
    /// Tells when the replay can start. Before it starts, no byte arrives. The launcher waits until the program enables
    /// the RBF interrupt, so that the program gets the first byte.
    /// </param>
    public ReplaySerialConnection(byte[] data, Func<bool>? canStart = null)
    {
        _data = data;
        _canStart = canStart ?? (() => true);
    }

    /// <summary>The number of bytes that the serial port took.</summary>
    public int Position { get; private set; }

    public int Length => _data.Length;

    /// <summary>True when the serial port took all the bytes.</summary>
    public bool Finished => Position >= _data.Length;

    public int Available => _started ? _data.Length - Position : 0;

    public bool TryRead(out byte value)
    {
        value = 0;
        if (Finished)
            return false;
        if (!_started && !(_started = _canStart()))
            return false;
        value = _data[Position++];
        return true;
    }

    public void Write(byte value)
    {
    }
}

/// <summary>
/// Writes each byte that goes through a serial connection to a log, with the time of the Amiga clock. The received
/// bytes (RX) are logged when the serial port takes them, and the sent bytes (TX) when the program writes them.
/// </summary>
/// <remarks>
/// A line has a maximum of 16 bytes of one direction. A new line starts when the direction changes, or after a gap
/// of 20 ms. Each line shows the time of its first byte, the offset of that byte in its direction, the bytes in hex
/// and the bytes as text.
/// </remarks>
public sealed class LoggingSerialConnection : ISerialConnection, IDisposable
{
    private const int BytesPerLine = 16;
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(20);

    private readonly ISerialConnection? _inner;
    private readonly TextWriter _log;
    private readonly Func<TimeSpan> _time;
    private readonly object _lock = new();
    private readonly List<byte> _line = [];
    private bool _lineIsReceived;
    private TimeSpan _lineStart;
    private TimeSpan _lastByte;
    private long _lineOffset;
    private long _received;
    private long _sent;

    /// <param name="inner">The connection to log. Null logs only the bytes that the Amiga sends.</param>
    public LoggingSerialConnection(ISerialConnection? inner, TextWriter log, Func<TimeSpan> time)
    {
        _inner = inner;
        _log = log;
        _time = time;
        _log.WriteLine("# time (s)    dir  offset    bytes                                            text");
    }

    public int Available => _inner?.Available ?? 0;

    public bool TryRead(out byte value)
    {
        value = 0;
        if (_inner == null || !_inner.TryRead(out value))
            return false;
        Add(value, received: true);
        return true;
    }

    public void Write(byte value)
    {
        Add(value, received: false);
        _inner?.Write(value);
    }

    /// <summary>Writes the last line.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            WriteLine();
            _log.Flush();
        }
    }

    private void Add(byte value, bool received)
    {
        lock (_lock)
        {
            var now = _time();
            if (_line.Count > 0 && (received != _lineIsReceived || _line.Count == BytesPerLine || now - _lastByte >= Gap))
                WriteLine();
            if (_line.Count == 0)
            {
                _lineIsReceived = received;
                _lineStart = now;
                _lineOffset = received ? _received : _sent;
            }

            _line.Add(value);
            _lastByte = now;
            if (received)
                _received++;
            else
                _sent++;
        }
    }

    private void WriteLine()
    {
        if (_line.Count == 0)
            return;
        var hex = string.Join(' ', _line.Select(value => value.ToString("X2")));
        var text = new StringBuilder(_line.Count);
        foreach (var value in _line)
            text.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
        _log.WriteLine($"{_lineStart.TotalSeconds,12:F6}  {(_lineIsReceived ? "RX" : "TX")}   {_lineOffset:X8}  {hex,-47}  {text}");
        _line.Clear();
    }
}
