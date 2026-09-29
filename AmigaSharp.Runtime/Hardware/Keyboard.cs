using System.Collections.Concurrent;

namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// The keyboard as the hardware sees it: it sends each key to the serial port of CIA-A. A program that takes over the
/// machine reads the keys from CIA-A SDR in its level 2 interrupt.
/// </summary>
/// <remarks>
/// The keyboard sends the raw key code, with bit 7 set when the key goes up. The byte is rotated left by one bit and
/// inverted, as on the cable. The keyboard sends the next byte after the program reads the ICR of CIA-A, or after a
/// timeout of 143 ms, as a real keyboard does when it gets no handshake.
/// </remarks>
public sealed class Keyboard(Cia cia, Func<long> eClock)
{
    private const long MinimumGap = 716;         // 1 ms of E clock.
    private const long HandshakeTimeout = 102_375; // 143 ms of E clock.

    private readonly ConcurrentQueue<byte> _codes = new();
    private long _sentAt = long.MinValue / 2;
    private bool _waiting;

    /// <summary>Adds a key event. The host thread can call this.</summary>
    public void Post(byte rawKey, bool up) => _codes.Enqueue((byte)(rawKey | (up ? 0x80 : 0)));

    /// <summary>Sends the next key to CIA-A when the last one is done.</summary>
    public void Update()
    {
        var now = eClock();
        if (now - _sentAt < MinimumGap)
            return;
        if (_waiting && !cia.SerialAcknowledged && now - _sentAt < HandshakeTimeout)
            return;
        if (!_codes.TryDequeue(out var code))
        {
            _waiting = false;
            return;
        }

        cia.ReceiveSerial((byte)~(code << 1 | code >> 7));
        _sentAt = now;
        _waiting = true;
    }
}
