namespace AmigaSharp.Runtime.Hardware;

/// <summary>The registers of an 8520 CIA.</summary>
public static class CiaRegister
{
    public const int Pra = 0x0;
    public const int Prb = 0x1;
    public const int Ddra = 0x2;
    public const int Ddrb = 0x3;
    public const int TodLow = 0x8;
    public const int TodMiddle = 0x9;
    public const int TodHigh = 0xA;
    public const int Icr = 0xD;
}

/// <summary>
/// An 8520 CIA. A port read gives the output bits where the data direction bit is 1, and the input pins where it is 0.
/// The time-of-day counter counts frames (CIA-A) or lines (CIA-B). The timers are not emulated: their registers keep
/// the values that the program wrote.
/// </summary>
public sealed class Cia(Func<long> timeOfDay)
{
    private readonly byte[] _registers = new byte[16];

    /// <summary>The levels of the input pins of port A. A pin that nothing drives is high.</summary>
    public byte InputA { get; set; } = 0xFF;

    /// <summary>The levels of the input pins of port B.</summary>
    public byte InputB { get; set; } = 0xFF;

    /// <summary>The value of the output bits of port A and port B, which the program wrote.</summary>
    public byte OutputA => _registers[CiaRegister.Pra];
    public byte OutputB => _registers[CiaRegister.Prb];

    public byte Read(int register)
    {
        switch (register)
        {
            case CiaRegister.Pra: return Port(_registers[CiaRegister.Pra], _registers[CiaRegister.Ddra], InputA);
            case CiaRegister.Prb: return Port(_registers[CiaRegister.Prb], _registers[CiaRegister.Ddrb], InputB);
            case CiaRegister.TodLow: return (byte)timeOfDay();
            case CiaRegister.TodMiddle: return (byte)(timeOfDay() >> 8);
            case CiaRegister.TodHigh: return (byte)(timeOfDay() >> 16);
            // No interrupt is pending. A read clears the flags.
            case CiaRegister.Icr: return 0;
            default: return _registers[register];
        }
    }

    public void Write(int register, byte value) => _registers[register] = value;

    private static byte Port(byte output, byte direction, byte input) => (byte)((output & direction) | (input & ~direction));
}
