namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// The battery-backed clock of the Amiga 2000: an OKI MSM6242B at $DC0000. It has 16 registers of 4 bits, one at each
/// fourth address. Registers 0 to 12 are the digits of the time and the date, and registers 13 to 15 are control
/// registers D, E and F.
/// </summary>
/// <remarks>
/// The time is the date of the emulated Amiga plus an offset. A write to a digit changes the offset, so a program can
/// set the clock. The year has two digits: 78 to 99 are 1978 to 1999, and 00 to 77 are 2000 to 2077. Register F bit 2
/// selects the 24-hour mode, which is on at the start. In the 12-hour mode, bit 2 of the tens of the hour is PM.
/// </remarks>
public sealed class RealTimeClockChip(Func<DateTime> now)
{
    private const int Seconds = 0, Minutes = 2, Hours = 4, Day = 6, Month = 8, Year = 10, Weekday = 12;
    private const int ControlD = 13, ControlE = 14, ControlF = 15;
    private const byte Hold = 1 << 0, Busy = 1 << 1;
    private const byte Reset = 1 << 0, Stop = 1 << 1, Mode24Hour = 1 << 2;
    private const byte PmBit = 1 << 2;

    // Kickstart sets the 24-hour mode at the start, so the clock of a running Amiga is in that mode.
    private readonly byte[] _control = CreateControl();
    private TimeSpan _offset;
    private DateTime? _stopped;

    /// <summary>Reads a register. The value is in the low 4 bits.</summary>
    public byte Read(int register)
    {
        if (register >= ControlD)
            return (byte)(_control[register] & (register == ControlD ? ~Busy : 0xF));

        var time = Time;
        return register switch
        {
            Seconds => (byte)(time.Second % 10),
            Seconds + 1 => (byte)(time.Second / 10),
            Minutes => (byte)(time.Minute % 10),
            Minutes + 1 => (byte)(time.Minute / 10),
            Hours => (byte)(DisplayHour(time) % 10),
            Hours + 1 => (byte)(DisplayHour(time) / 10 | (!Is24Hour && time.Hour >= 12 ? PmBit : 0)),
            Day => (byte)(time.Day % 10),
            Day + 1 => (byte)(time.Day / 10),
            Month => (byte)(time.Month % 10),
            Month + 1 => (byte)(time.Month / 10),
            Year => (byte)(time.Year % 10),
            Year + 1 => (byte)(time.Year / 10 % 10),
            Weekday => (byte)time.DayOfWeek,
            _ => 0,
        };
    }

    /// <summary>Writes the low 4 bits of the value to a register.</summary>
    public void Write(int register, byte value)
    {
        value &= 0xF;
        if (register >= ControlD)
        {
            WriteControl(register, value);
            return;
        }

        var time = Time;
        int Digit(int tens, int ones, int written, bool isTens) =>
            isTens ? written * 10 + ones : tens * 10 + written;
        var isTens = register % 2 == 1;
        var digitRegister = register & ~1;
        try
        {
            var changed = digitRegister switch
            {
                Seconds => time.AddSeconds(Digit(time.Second / 10, time.Second % 10, value, isTens) - time.Second),
                Minutes => time.AddMinutes(Digit(time.Minute / 10, time.Minute % 10, value, isTens) - time.Minute),
                Hours => time.AddHours(WrittenHour(time, value, isTens) - time.Hour),
                Day => new DateTime(time.Year, time.Month,
                    Math.Clamp(Digit(time.Day / 10, time.Day % 10, value, isTens), 1,
                        DateTime.DaysInMonth(time.Year, time.Month)), time.Hour, time.Minute, time.Second),
                Month => new DateTime(time.Year, Math.Clamp(Digit(time.Month / 10, time.Month % 10, value, isTens), 1, 12),
                    1, time.Hour, time.Minute, time.Second).AddDays(time.Day - 1),
                Year => time.AddYears(FullYear(Digit(time.Year / 10 % 10, time.Year % 10, value, isTens)) - time.Year),
                // The weekday follows from the date.
                _ => time,
            };
            SetTime(changed);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A digit that makes no date, for example day 31 in April, keeps the old time.
        }
    }

    private bool Is24Hour => (_control[ControlF] & Mode24Hour) != 0;

    private DateTime Time => _stopped ?? now() + _offset;

    private void SetTime(DateTime time)
    {
        if (_stopped != null)
            _stopped = time;
        else
            _offset = time - now();
    }

    private void WriteControl(int register, byte value)
    {
        _control[register] = value;
        if (register != ControlF)
            return;
        // STOP and RESET stop the clock. RESET also clears the seconds.
        if ((value & (Stop | Reset)) != 0)
        {
            _stopped ??= Time;
            if ((value & Reset) != 0)
                _stopped = _stopped.Value.AddSeconds(-_stopped.Value.Second);
        }
        else if (_stopped is { } stopped)
        {
            _stopped = null;
            _offset = stopped - now();
        }
    }

    private int DisplayHour(DateTime time) => Is24Hour ? time.Hour : (time.Hour % 12 == 0 ? 12 : time.Hour % 12);

    private int WrittenHour(DateTime time, byte value, bool isTens)
    {
        if (Is24Hour)
            return isTens ? value * 10 + time.Hour % 10 : time.Hour / 10 * 10 + value;
        var pm = isTens ? (value & PmBit) != 0 : time.Hour >= 12;
        var hour12 = isTens ? (value & 3) * 10 + DisplayHour(time) % 10 : DisplayHour(time) / 10 * 10 + value;
        return hour12 % 12 + (pm ? 12 : 0);
    }

    private static byte[] CreateControl()
    {
        var control = new byte[16];
        control[ControlF] = Mode24Hour;
        return control;
    }

        private static int FullYear(int twoDigits) => twoDigits >= 78 ? 1900 + twoDigits : 2000 + twoDigits;
}
