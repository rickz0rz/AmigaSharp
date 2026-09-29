namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// A slow serial line on one input pin of a CIA, for a program that reads the line bit by bit. For example, Prevue
/// reads a 110 baud control line on the CTS pin of the serial port (CIA-B port A bit 4) in its audio interrupt.
/// </summary>
/// <remarks>
/// The line sends each byte of <see cref="Connection"/> as a UART does: a start bit (low), 8 data bits (the lowest
/// bit first), and a stop bit (high). The line is high while it has no byte to send. The time of each bit comes from
/// <see cref="Time"/>, so the program sees the bits at the right time also with a virtual clock.
/// </remarks>
public sealed class BitBangedLine(Cia cia, byte pin, Func<TimeSpan> time, int baud = 110)
{
    private const int BitsPerByte = 10;
    private double _now;
    private double _byteStart = -1;
    private int _byte;

    /// <summary>
    /// The time of the line. The default is the clock of the emulation. For a program that samples the line in an
    /// interrupt, use the time of the last request of that interrupt. Then the program sees each bit at its time,
    /// also when the runtime is late and the interrupts come close together.
    /// </summary>
    public Func<TimeSpan> Time { get; set; } = time;

    /// <summary>The source of the bytes, for example a TCP bridge or a replay of a file. Null for an idle line.</summary>
    public ISerialConnection? Connection { get; set; }

    /// <summary>
    /// Tells if the program reads the line. Until then, the line keeps the bytes of <see cref="Connection"/>, because
    /// the program would lose them. Null if the program always reads the line.
    /// </summary>
    public Func<bool>? Ready { get; set; }

    /// <summary>The number of bytes that the line started to send.</summary>
    public long BytesSent { get; private set; }

    /// <summary>Sets the level of the pin for the current time. The runtime calls this at each safe point.</summary>
    public void Update()
    {
        // The time never goes back, also when the source of the time changes.
        _now = Math.Max(_now, Time().TotalSeconds);
        var bitTime = 1.0 / baud;
        if (_byteStart >= 0 && _now >= _byteStart + BitsPerByte * bitTime)
            _byteStart = -1;
        if (_byteStart < 0 && Ready?.Invoke() != false && Connection?.TryRead(out var value) == true)
        {
            _byte = value;
            _byteStart = _now;
            BytesSent++;
        }

        var high = true;
        if (_byteStart >= 0)
        {
            var bit = (int)((_now - _byteStart) / bitTime);
            high = bit switch
            {
                0 => false,
                <= 8 => ((_byte >> (bit - 1)) & 1) != 0,
                _ => true,
            };
        }

        cia.SetInputPinsA(pin, high ? pin : (byte)0);
    }
}
