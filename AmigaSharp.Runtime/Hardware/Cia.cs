namespace AmigaSharp.Runtime.Hardware;

/// <summary>The registers of an 8520 CIA.</summary>
public static class CiaRegister
{
    public const int Pra = 0x0;
    public const int Prb = 0x1;
    public const int Ddra = 0x2;
    public const int Ddrb = 0x3;
    public const int TaLow = 0x4;
    public const int TaHigh = 0x5;
    public const int TbLow = 0x6;
    public const int TbHigh = 0x7;
    public const int TodLow = 0x8;
    public const int TodMiddle = 0x9;
    public const int TodHigh = 0xA;
    public const int Sdr = 0xC;
    public const int Icr = 0xD;
    public const int Cra = 0xE;
    public const int Crb = 0xF;
}

/// <summary>The bits of the interrupt control register (ICR) of a CIA.</summary>
public static class CiaInterrupt
{
    public const byte TimerA = 1 << 0;
    public const byte TimerB = 1 << 1;
    public const byte Alarm = 1 << 2;
    public const byte SerialPort = 1 << 3;
    public const byte Flag = 1 << 4;

    /// <summary>In a read: an enabled interrupt is pending. In a write: set the mask bits, not clear them.</summary>
    public const byte SetClear = 1 << 7;
}

/// <summary>
/// An 8520 CIA. A port read gives the output bits where the data direction bit is 1, and the input pins where it is 0.
/// The time-of-day counter counts frames (CIA-A) or lines (CIA-B), and it has the latch and the alarm of the 8520.
/// Timer A and timer B count down at the E clock.
/// </summary>
/// <remarks>
/// The timers count lazily: a timer computes its value from the E clock when the program reads it, and when the
/// runtime calls <see cref="Update"/> at a safe point. An underflow sets its flag in the ICR. If the ICR mask enables
/// the flag, the CIA calls the interrupt action: CIA-A requests the PORTS interrupt and CIA-B the EXTER interrupt.
/// A timer counts only the E clock, or timer B the underflows of timer A. The serial port only receives bytes, from
/// <see cref="ReceiveSerial"/>. The CNT pin is not emulated.
/// </remarks>
/// <param name="timeOfDay">The value of the time-of-day counter.</param>
/// <param name="eClock">The number of E clock cycles from the start.</param>
/// <param name="interrupt">Requests the interrupt of the CIA from Paula.</param>
public sealed class Cia(Func<long> timeOfDay, Func<long> eClock, Action interrupt)
{
    // The bits of CRA and CRB.
    private const byte Start = 1 << 0;
    private const byte RunModeOneShot = 1 << 3;
    private const byte ForceLoad = 1 << 4;
    private const byte InputModeMask = 0x60;
    private const byte TimerBCountsTimerA = 0x40;
    private const byte CrbWritesAlarm = 0x80;
    private const int TodMask = 0xFF_FFFF;

    private readonly byte[] _registers = new byte[16];
    private readonly Timer _timerA = new();
    private readonly Timer _timerB = new();
    private long _lastUpdate;
    private byte _interruptFlags;
    private byte _interruptMask;
    private bool _serialUnread;

    // The time-of-day counter is the source count plus an offset, so that a write can set it. A write of the high byte
    // stops the counter at _todStopped until a write of the low byte. A read of the high byte latches the value in
    // _todLatch until a read of the low byte.
    private long _todOffset;
    private int? _todStopped;
    private int? _todLatch;
    private int _todAlarm;
    private int _lastTod;

    /// <summary>The levels of the input pins of port A. A pin that nothing drives is high.</summary>
    public byte InputA { get; set; } = 0xFF;

    /// <summary>The levels of the input pins of port B.</summary>
    public byte InputB { get; set; } = 0xFF;

    private readonly object _pinLock = new();

    /// <summary>
    /// Sets the levels of some input pins of port A. More than one device drives the pins of port A, from more than
    /// one thread, so each device changes only its own pins.
    /// </summary>
    public void SetInputPinsA(byte mask, byte levels)
    {
        lock (_pinLock)
            InputA = (byte)((InputA & ~mask) | (levels & mask));
    }

    /// <summary>The program changed port B or its directions. The value is the level of each pin.</summary>
    public event Action<byte>? PortBChanged;

    /// <summary>The FLAG input went low, for example at the index pulse of a disk. It sets the FLG flag of the ICR.</summary>
    public void SignalFlag()
    {
        _interruptFlags |= CiaInterrupt.Flag;
        RequestIfEnabled();
    }

    /// <summary>The value of the output bits of port A and port B, which the program wrote.</summary>
    public byte OutputA => _registers[CiaRegister.Pra];
    public byte OutputB => _registers[CiaRegister.Prb];

    public byte Read(int register)
    {
        switch (register)
        {
            case CiaRegister.Pra: return Port(_registers[CiaRegister.Pra], _registers[CiaRegister.Ddra], InputA);
            case CiaRegister.Prb: return Port(_registers[CiaRegister.Prb], _registers[CiaRegister.Ddrb], InputB);
            case CiaRegister.TaLow: Update(); return (byte)_timerA.Counter;
            case CiaRegister.TaHigh: Update(); return (byte)(_timerA.Counter >> 8);
            case CiaRegister.TbLow: Update(); return (byte)_timerB.Counter;
            case CiaRegister.TbHigh: Update(); return (byte)(_timerB.Counter >> 8);
            case CiaRegister.TodLow:
            {
                var value = _todLatch ?? TimeOfDay;
                _todLatch = null;
                return (byte)value;
            }
            case CiaRegister.TodMiddle: return (byte)((_todLatch ?? TimeOfDay) >> 8);
            case CiaRegister.TodHigh:
                _todLatch = TimeOfDay;
                return (byte)(_todLatch.Value >> 16);
            case CiaRegister.Icr:
            {
                // A read gives the flags and clears them.
                Update();
                var value = _interruptFlags;
                if ((_interruptFlags & _interruptMask) != 0)
                    value |= CiaInterrupt.SetClear;
                _interruptFlags = 0;
                _serialUnread = false;
                return value;
            }
            // START shows if the timer runs: a one-shot timer clears it at its underflow.
            case CiaRegister.Cra: Update(); return Control(CiaRegister.Cra, _timerA);
            case CiaRegister.Crb: Update(); return Control(CiaRegister.Crb, _timerB);
            default: return _registers[register];
        }
    }

    public void Write(int register, byte value)
    {
        Update();
        switch (register)
        {
            case CiaRegister.TaLow: _timerA.Latch = (ushort)((_timerA.Latch & 0xFF00) | value); break;
            case CiaRegister.TaHigh: WriteHigh(_timerA, CiaRegister.Cra, value); break;
            case CiaRegister.TbLow: _timerB.Latch = (ushort)((_timerB.Latch & 0xFF00) | value); break;
            case CiaRegister.TbHigh: WriteHigh(_timerB, CiaRegister.Crb, value); break;
            case CiaRegister.Icr:
                if ((value & CiaInterrupt.SetClear) != 0)
                    _interruptMask |= (byte)(value & 0x1F);
                else
                    _interruptMask &= (byte)~value;
                RequestIfEnabled();
                break;
            case CiaRegister.Cra: WriteControl(_timerA, value); break;
            case CiaRegister.Crb: WriteControl(_timerB, value); break;
            case CiaRegister.TodLow or CiaRegister.TodMiddle or CiaRegister.TodHigh:
                WriteTimeOfDay(register, value);
                break;
        }

        _registers[register] = value;
        if (register is CiaRegister.Prb or CiaRegister.Ddrb)
            PortBChanged?.Invoke(Port(_registers[CiaRegister.Prb], _registers[CiaRegister.Ddrb], InputB));
    }

    /// <summary>The value of the time-of-day counter: 24 bits.</summary>
    private int TimeOfDay => _todStopped ?? (int)((timeOfDay() + _todOffset) & TodMask);

    private void WriteTimeOfDay(int register, byte value)
    {
        var shift = (register - CiaRegister.TodLow) * 8;
        if ((_registers[CiaRegister.Crb] & CrbWritesAlarm) != 0)
        {
            _todAlarm = (_todAlarm & ~(0xFF << shift)) | (value << shift);
            return;
        }

        var current = TimeOfDay;
        var changed = (current & ~(0xFF << shift)) | (value << shift);
        if (register == CiaRegister.TodHigh || (_todStopped != null && register == CiaRegister.TodMiddle))
        {
            _todStopped = changed;
            return;
        }

        // A write of the low byte (or of the middle byte of a running counter) sets the counter and starts it.
        _todStopped = null;
        _todOffset = changed - timeOfDay();
        _lastTod = changed;
    }

    /// <summary>Sets the alarm flag if the time-of-day counter passed the alarm value since the last check.</summary>
    private void CheckAlarm()
    {
        var now = TimeOfDay;
        var passed = (now - _lastTod) & TodMask;
        if (passed != 0 && ((_todAlarm - _lastTod - 1) & TodMask) < passed)
            _interruptFlags |= CiaInterrupt.Alarm;
        _lastTod = now;
    }

    /// <summary>True if the program read the ICR after the last byte came in on the serial port.</summary>
    public bool SerialAcknowledged => !_serialUnread;

    /// <summary>A byte came in on the serial port (the keyboard of CIA-A). It goes to SDR and sets the SP flag.</summary>
    public void ReceiveSerial(byte value)
    {
        _registers[CiaRegister.Sdr] = value;
        _interruptFlags |= CiaInterrupt.SerialPort;
        _serialUnread = true;
        RequestIfEnabled();
    }

    /// <summary>Counts the timers to the current E clock, and requests the interrupt of each new enabled flag.</summary>
    public void Update()
    {
        CheckAlarm();
        RequestIfEnabled();
        var now = eClock();
        var cycles = now - _lastUpdate;
        if (cycles <= 0)
            return;
        _lastUpdate = now;

        var underflowsA = _timerA.Running ? _timerA.Count(cycles) : 0;
        if (underflowsA > 0)
            _interruptFlags |= CiaInterrupt.TimerA;

        var timerBInput = _registers[CiaRegister.Crb] & InputModeMask;
        var timerBEvents = timerBInput switch
        {
            0 => cycles,
            TimerBCountsTimerA => underflowsA,
            _ => 0,
        };
        if (_timerB.Running && timerBEvents > 0 && _timerB.Count(timerBEvents) > 0)
            _interruptFlags |= CiaInterrupt.TimerB;

        RequestIfEnabled();
    }

    private void RequestIfEnabled()
    {
        if ((_interruptFlags & _interruptMask) != 0)
            interrupt();
    }

    private static void WriteHigh(Timer timer, int controlRegister, byte value)
    {
        timer.Latch = (ushort)((timer.Latch & 0x00FF) | (value << 8));
        // A write of the high byte loads a stopped timer. In one-shot mode, it also starts the timer.
        if (!timer.Running)
            timer.Counter = timer.Latch;
        if (timer.OneShot)
        {
            timer.Counter = timer.Latch;
            timer.Running = true;
        }
    }

    private static void WriteControl(Timer timer, byte value)
    {
        timer.OneShot = (value & RunModeOneShot) != 0;
        if ((value & ForceLoad) != 0)
            timer.Counter = timer.Latch;
        timer.Running = (value & Start) != 0;
    }

    private byte Control(int register, Timer timer) =>
        (byte)((_registers[register] & ~(ForceLoad | Start)) | (timer.Running ? Start : 0));

    private static byte Port(byte output, byte direction, byte input) => (byte)((output & direction) | (input & ~direction));

    /// <summary>A 16-bit timer. It counts from the latch value down to 0, and then loads the latch again.</summary>
    private sealed class Timer
    {
        public ushort Latch = 0xFFFF;
        public ushort Counter = 0xFFFF;
        public bool Running;
        public bool OneShot;

        /// <summary>Counts the events. Returns the number of underflows. A one-shot timer stops at its underflow.</summary>
        public long Count(long events)
        {
            if (events <= Counter)
            {
                Counter -= (ushort)events;
                return 0;
            }

            // The first underflow comes after Counter + 1 events. Then each underflow comes after Latch + 1 events.
            events -= Counter + 1L;
            if (OneShot)
            {
                Counter = Latch;
                Running = false;
                return 1;
            }

            var period = Latch + 1L;
            Counter = (ushort)(Latch - events % period);
            return 1 + events / period;
        }
    }
}
