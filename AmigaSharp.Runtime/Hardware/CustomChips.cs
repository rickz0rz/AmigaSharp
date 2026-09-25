namespace AmigaSharp.Runtime.Hardware;

/// <summary>The offsets of the custom chip registers from $DFF000 (hardware/custom.h).</summary>
public static class CustomRegister
{
    public const uint Base = 0xDF_F000;

    public const int Bltddat = 0x000;
    public const int Dmaconr = 0x002;
    public const int Vposr = 0x004;
    public const int Vhposr = 0x006;
    public const int Joy0dat = 0x00A;
    public const int Joy1dat = 0x00C;
    public const int Adkconr = 0x010;
    public const int Pot0dat = 0x012;
    public const int Pot1dat = 0x014;
    public const int Potgor = 0x016;
    public const int Serdatr = 0x018;
    public const int Dskbytr = 0x01A;
    public const int Intenar = 0x01C;
    public const int Intreqr = 0x01E;
    public const int Serdat = 0x030;
    public const int Serper = 0x032;
    public const int Cop1lc = 0x080;
    public const int Cop2lc = 0x084;
    public const int Copjmp1 = 0x088;
    public const int Copjmp2 = 0x08A;
    public const int Diwstrt = 0x08E;
    public const int Diwstop = 0x090;
    public const int Ddfstrt = 0x092;
    public const int Ddfstop = 0x094;
    public const int Dmacon = 0x096;
    public const int Intena = 0x09A;
    public const int Intreq = 0x09C;
    public const int Adkcon = 0x09E;
    public const int Bpl1pt = 0x0E0;
    public const int Bplcon0 = 0x100;
    public const int Bplcon1 = 0x102;
    public const int Bplcon2 = 0x104;
    public const int Bpl1mod = 0x108;
    public const int Bpl2mod = 0x10A;
    public const int Deniseid = 0x07C;
    public const int Color00 = 0x180;
}

/// <summary>
/// The custom chips (Agnus, Denise and Paula) as registers. The model stores each write, so the display and the
/// devices can read the values. DMACON, INTENA, INTREQ and ADKCON use the set/clear bit 15. A read of a register that
/// the model does not have throws <see cref="HardwareAccessException"/>.
/// </summary>
public sealed class CustomChips(Beam beam)
{
    /// <summary>The Agnus ID in VPOSR bits 14 to 8. $30 is the ECS Agnus (8372) for NTSC.</summary>
    public int AgnusId { get; set; } = 0x30;

    private readonly ushort[] _registers = new ushort[0x100];

    /// <summary>The value that the program last wrote to the register.</summary>
    public ushort this[int offset] => _registers[offset >> 1];

    /// <summary>The program wrote a word to SERDAT.</summary>
    public event Action<ushort>? SerialTransmit;

    /// <summary>The program wrote to COPJMP1 or COPJMP2.</summary>
    public event Action<int>? CopperJump;

    public ushort Dmacon { get; private set; }
    public ushort Intena { get; private set; }
    public ushort Intreq { get; private set; }
    public ushort Adkcon { get; private set; }

    /// <summary>The value of SERDATR. The serial port model sets it.</summary>
    public Func<ushort> SerialReceive { get; set; } = () => 0x3000;

    public ushort Read(int offset)
    {
        switch (offset)
        {
            case CustomRegister.Bltddat: return 0;
            case CustomRegister.Dmaconr: return (ushort)(Dmacon & 0x07FF);
            case CustomRegister.Vposr:
                // Bit 15 is the long frame flag, bits 14 to 8 are the Agnus ID, and bit 0 is bit 8 of the line.
                return (ushort)(0x8000 | (AgnusId << 8) | (beam.Line >> 8));
            case CustomRegister.Vhposr: return (ushort)(((beam.Line & 0xFF) << 8) | beam.Horizontal);
            case CustomRegister.Joy0dat or CustomRegister.Joy1dat: return 0;
            case CustomRegister.Adkconr: return Adkcon;
            case CustomRegister.Pot0dat or CustomRegister.Pot1dat: return 0;
            // The pins are inputs and the right mouse buttons are not pressed.
            case CustomRegister.Potgor: return 0xFF00;
            case CustomRegister.Serdatr: return SerialReceive();
            case CustomRegister.Dskbytr: return 0;
            case CustomRegister.Intenar: return Intena;
            case CustomRegister.Intreqr: return Intreq;
            // The ECS Denise (8373) has the ID $FC.
            case CustomRegister.Deniseid: return 0xFFFC;
            default: throw new HardwareAccessException(CustomRegister.Base + (uint)offset);
        }
    }

    public void Write(int offset, ushort value)
    {
        _registers[offset >> 1] = value;
        switch (offset)
        {
            case CustomRegister.Dmacon: Dmacon = SetClear(Dmacon, value); break;
            case CustomRegister.Intena: Intena = SetClear(Intena, value); break;
            case CustomRegister.Intreq: Intreq = SetClear(Intreq, value); break;
            case CustomRegister.Adkcon: Adkcon = SetClear(Adkcon, value); break;
            case CustomRegister.Serdat: SerialTransmit?.Invoke(value); break;
            case CustomRegister.Copjmp1: CopperJump?.Invoke(1); break;
            case CustomRegister.Copjmp2: CopperJump?.Invoke(2); break;
        }
    }

    /// <summary>Sets an interrupt request, as the hardware does. For example, the start of each frame sets VERTB.</summary>
    public void RequestInterrupt(int bit) => Intreq |= (ushort)(1 << bit);

    /// <summary>Bit 15 set: the other bits that are 1 are set. Bit 15 clear: they are cleared.</summary>
    private static ushort SetClear(ushort old, ushort value) =>
        (value & 0x8000) != 0 ? (ushort)(old | (value & 0x7FFF)) : (ushort)(old & ~value);
}
