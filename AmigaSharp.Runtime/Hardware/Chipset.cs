namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// Connects the hardware addresses to the models: the custom chips at $DFF000 and the two CIAs at $BFxxxx. An access
/// to another hardware address throws <see cref="HardwareAccessException"/>.
/// </summary>
/// <remarks>
/// CIA-A uses the odd addresses (the low byte of the data bus) and responds when address bit 12 is 0. CIA-B uses the
/// even addresses (the high byte) and responds when address bit 13 is 0. Address bits 11 to 8 select the register.
/// </remarks>
public sealed class Chipset : IHardware
{
    private const uint CustomStart = 0xDF_F000;
    private const uint CustomEnd = 0xDF_F200;
    private const uint CiaStart = 0xBF_0000;
    private const uint CiaEnd = 0xC0_0000;

    public Chipset(IClock clock)
    {
        Beam = new Beam(clock);
        Custom = new CustomChips(Beam);
        CiaA = new Cia(() => Beam.Frame);
        CiaB = new Cia(() => Beam.TotalLines);
    }

    public Beam Beam { get; }
    public CustomChips Custom { get; }
    public Cia CiaA { get; }
    public Cia CiaB { get; }

    public byte Read8(uint address)
    {
        if (IsCustom(address))
        {
            var word = Custom.Read((int)(address - CustomStart) & ~1);
            return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        if (IsCia(address))
        {
            if ((address & 1) != 0)
                return (address & 0x1000) == 0 ? CiaA.Read(CiaRegisterOf(address)) : (byte)0xFF;
            return (address & 0x2000) == 0 ? CiaB.Read(CiaRegisterOf(address)) : (byte)0xFF;
        }

        throw new HardwareAccessException(address);
    }

    public ushort Read16(uint address)
    {
        if (IsCustom(address))
            return Custom.Read((int)(address - CustomStart));
        if (IsCia(address))
        {
            var high = (address & 0x2000) == 0 ? CiaB.Read(CiaRegisterOf(address)) : (byte)0xFF;
            var low = (address & 0x1000) == 0 ? CiaA.Read(CiaRegisterOf(address)) : (byte)0xFF;
            return (ushort)(high << 8 | low);
        }

        throw new HardwareAccessException(address);
    }

    public void Write8(uint address, byte value)
    {
        if (IsCustom(address))
        {
            // A byte write to a custom chip register puts the byte in both halves of the word.
            Custom.Write((int)(address - CustomStart) & ~1, (ushort)(value << 8 | value));
            return;
        }

        if (IsCia(address))
        {
            if ((address & 1) != 0 && (address & 0x1000) == 0)
                CiaA.Write(CiaRegisterOf(address), value);
            else if ((address & 1) == 0 && (address & 0x2000) == 0)
                CiaB.Write(CiaRegisterOf(address), value);
            return;
        }

        throw new HardwareAccessException(address);
    }

    public void Write16(uint address, ushort value)
    {
        if (IsCustom(address))
        {
            Custom.Write((int)(address - CustomStart), value);
            return;
        }

        if (IsCia(address))
        {
            if ((address & 0x2000) == 0)
                CiaB.Write(CiaRegisterOf(address), (byte)(value >> 8));
            if ((address & 0x1000) == 0)
                CiaA.Write(CiaRegisterOf(address), (byte)value);
            return;
        }

        throw new HardwareAccessException(address);
    }

    private static bool IsCustom(uint address) => address is >= CustomStart and < CustomEnd;

    private static bool IsCia(uint address) => address is >= CiaStart and < CiaEnd;

    private static int CiaRegisterOf(uint address) => (int)(address >> 8) & 0xF;
}
