namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// Connects the hardware addresses to the models: the custom chips at $DFF000 and the two CIAs at $BFxxxx. An access
/// to another hardware address throws <see cref="HardwareAccessException"/>.
/// </summary>
/// <remarks>
/// The battery-backed clock is at $DC0000.
/// An Amiga 2000 with 8 MB of Zorro II memory has nothing at $A00000 to $BEFFFF, $C00000 to $DBFFFF (no slow memory),
/// $DD0000 to $DEFFFF (the system registers of the later models: Gary, Ramsey and Gayle) and $E00000 to $F7FFFF (no
/// expansion boards to configure). A program that looks for memory or devices there gets an open bus: a write does
/// nothing, and a read gives 0.
/// </remarks>
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

    public Chipset(IClock clock, Memory memory)
    {
        Beam = new Beam(clock);
        Custom = new CustomChips(Beam);
        long EClock() => (long)(Beam.Clock.Elapsed.TotalSeconds * Cia.NtscEClockHz);
        CiaA = new Cia(() => Beam.Frame, EClock, () => Custom.RequestInterrupt(InterruptBit.Ports));
        CiaB = new Cia(() => Beam.TotalLines, EClock, () => Custom.RequestInterrupt(InterruptBit.External));
        Keyboard = new Keyboard(CiaA, EClock);
        // The left mouse button or fire button of each port is an input of CIA-A port A: bit 6 for port 1 and bit 7
        // for port 2. A pressed button connects the pin to ground.
        for (var port = 0; port < 2; port++)
        {
            var controller = Custom.Ports[port];
            var bit = 1 << (6 + port);
            controller.ButtonChanged += () =>
                CiaA.SetInputPinsA((byte)bit, controller.IsPressed(ControllerButton.Left) ? (byte)0 : (byte)bit);
        }
        Rtc = new RealTimeClockChip(() => Now());
        // The low-pass filter is on when the power LED is bright: CIA-A PRA bit 1 is 0.
        Audio = new AudioOutput(memory, Custom, () => (CiaA.OutputA & 0x02) == 0);
        Disks = new DiskController(memory, Custom, CiaA, CiaB, Beam);
        // The handshake inputs of the serial port: CTS is CIA-B port A bit 4, and DSR is bit 3.
        CtsLine = new BitBangedLine(CiaB, 0x10, () => Beam.Clock.Elapsed);
        DsrLine = new BitBangedLine(CiaB, 0x08, () => Beam.Clock.Elapsed);
        Display = new Display(memory, Custom, Beam);
        Custom.Blitter = new Blitter(memory, Custom);
        Custom.FrameEnded += Display.RunFrame;
        Custom.FrameStarted += Display.FrameStarted;
        Custom.RegisterWritten += Display.RegisterWritten;
        Custom.BeamMoved += Display.CatchUp;
        Custom.CopperJump += Display.CopperJumped;
    }

    public Beam Beam { get; }
    public CustomChips Custom { get; }
    public Cia CiaA { get; }
    public Cia CiaB { get; }

    /// <summary>The date and the time of the Amiga, for the battery-backed clock. The core sets it.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    /// <summary>
    /// A slow serial line on the CTS pin of the serial port (CIA-B port A bit 4), for a program that reads the pin bit
    /// by bit. The line is idle (high) until the launcher gives it a connection. For example, Prevue reads its 110 baud
    /// control line (CTRL) there: the commands that show the promos and the logos in the top half of the screen.
    /// </summary>
    public BitBangedLine CtsLine { get; }

    /// <summary>
    /// A slow serial line on the DSR pin of the serial port (CIA-B port A bit 3), as <see cref="CtsLine"/>. For
    /// example, Prevue reads its operator console there.
    /// </summary>
    public BitBangedLine DsrLine { get; }

    /// <summary>The floppy drives and the disk DMA.</summary>
    public DiskController Disks { get; }

    /// <summary>The sound of the audio channels.</summary>
    public AudioOutput Audio { get; }

    /// <summary>The battery-backed clock at $DC0000.</summary>
    public RealTimeClockChip Rtc { get; }

    /// <summary>The keyboard on the serial port of CIA-A.</summary>
    public Keyboard Keyboard { get; }

    /// <summary>The picture that the custom chips make.</summary>
    public Display Display { get; }

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

        if (IsRtc(address))
            return (address & 1) != 0 ? Rtc.Read(RtcRegisterOf(address)) : (byte)0;
        if (IsOpenBus(address))
            return 0;
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

        if (IsRtc(address))
            return Rtc.Read(RtcRegisterOf(address));
        if (IsOpenBus(address))
            return 0;
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

        if (IsRtc(address))
        {
            Rtc.Write(RtcRegisterOf(address), (byte)value);
            return;
        }

        if (IsOpenBus(address))
            return;
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

        if (IsRtc(address))
        {
            Rtc.Write(RtcRegisterOf(address), (byte)value);
            return;
        }

        if (IsOpenBus(address))
            return;
        throw new HardwareAccessException(address);
    }

    private static bool IsCustom(uint address) => address is >= CustomStart and < CustomEnd;

    // The clock uses the low 4 bits of the data bus. Its registers are at each fourth address from $DC0000.
    private static bool IsRtc(uint address) => address is >= 0xDC_0000 and < 0xDD_0000;

    private static int RtcRegisterOf(uint address) => (int)(address >> 2) & 0xF;

    private static bool IsOpenBus(uint address) =>
        address is >= 0xA0_0000 and < 0xBF_0000 or >= 0xC0_0000 and < 0xDF_0000 or >= 0xE0_0000 and < 0xF8_0000;

    private static bool IsCia(uint address) => address is >= CiaStart and < CiaEnd;

    private static int CiaRegisterOf(uint address) => (int)(address >> 8) & 0xF;
}
