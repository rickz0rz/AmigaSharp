using System.Collections.Concurrent;

namespace AmigaSharp.Runtime.Hardware;

/// <summary>The other end of the serial cable: a TCP client, a file, or a test.</summary>
public interface ISerialConnection
{
    /// <summary>Gets the next byte that the other end sent, if a byte is available.</summary>
    bool TryRead(out byte value);

    /// <summary>Sends a byte to the other end. The byte is lost if nothing is connected.</summary>
    void Write(byte value);

    /// <summary>The number of received bytes that are not read yet.</summary>
    int Available { get; }
}

/// <summary>A serial connection in memory, for tests.</summary>
public sealed class MemorySerialConnection : ISerialConnection
{
    private readonly ConcurrentQueue<byte> _received = new();

    /// <summary>The bytes that the Amiga sent.</summary>
    public ConcurrentQueue<byte> Sent { get; } = new();

    /// <summary>Makes bytes available, as if the other end sent them.</summary>
    public void Send(params byte[] bytes)
    {
        foreach (var value in bytes)
            _received.Enqueue(value);
    }

    public int Available => _received.Count;

    public bool TryRead(out byte value) => _received.TryDequeue(out value);

    public void Write(byte value) => Sent.Enqueue(value);
}

/// <summary>
/// The UART of Paula. A received byte goes to SERDATR and sets the RBF interrupt request. The next byte waits until
/// the program clears RBF and the time of one byte at the baud rate has passed. So the program cannot lose a byte,
/// as it can on real hardware. A word that the program writes to SERDAT goes to the connection at once, and TBE
/// becomes set.
/// </summary>
public sealed class SerialPort(Beam beam)
{
    /// <summary>INTREQ bit 0: the transmit buffer is empty.</summary>
    public const int InterruptTbe = 0;

    /// <summary>INTREQ bit 11: the receive buffer is full.</summary>
    public const int InterruptRbf = 11;

    private ushort _received;
    private TimeSpan _nextReceive;

    /// <summary>The other end of the cable. Null means that the cable is not connected.</summary>
    public ISerialConnection? Connection { get; set; }

    /// <summary>SERPER: bits 14 to 0 are the period in color clocks minus 1. Bit 15 selects 9 data bits.</summary>
    public ushort Period { get; set; }

    /// <summary>
    /// The speed of the line as a factor of the baud rate. A value above 1 receives the bytes faster than a real
    /// serial port can, for example to send a large data feed. The program must parse the bytes fast enough.
    /// </summary>
    public double SpeedFactor { get; set; } = 1;

    /// <summary>The baud rate that SERPER gives.</summary>
    public double BaudRate => Beam.ColorClockHz / ((Period & 0x7FFF) + 1);

    /// <summary>The time of one byte: a start bit, 8 or 9 data bits and a stop bit.</summary>
    private TimeSpan ByteTime => TimeSpan.FromSeconds(((Period & 0x8000) != 0 ? 11 : 10) / BaudRate);

    /// <summary>
    /// SERDATR: bit 14 is RBF, bits 13 and 12 are TBE and TSRE (the transmitter is always empty), bit 11 is the level
    /// of the receive line (1, idle), and the low bits are the data and the stop bit.
    /// </summary>
    public ushort ReadData(ushort intreq) =>
        (ushort)(((intreq >> InterruptRbf) & 1) << 14 | 0x3800 | _received);

    /// <summary>Moves the next byte to SERDATR if RBF is clear and the byte time has passed.</summary>
    /// <returns>True if a byte arrived. The caller then sets the RBF interrupt request.</returns>
    public bool Update(bool rbfPending)
    {
        if (rbfPending || Connection == null)
            return false;
        var now = beam.Clock.Elapsed;
        if (now < _nextReceive || !Connection.TryRead(out var value))
            return false;

        // In 8-bit mode, bit 8 is the stop bit. In 9-bit mode, bit 9 is the stop bit.
        _received = (ushort)(value | ((Period & 0x8000) != 0 ? 0x200 : 0x100));
        _nextReceive = now + ByteTime / SpeedFactor;
        return true;
    }

    /// <summary>SERDAT: sends the data bits. The stop bits are not sent.</summary>
    public void WriteData(ushort value) => Connection?.Write((byte)value);
}
