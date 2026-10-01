using AmigaSharp.Runtime.Cpu;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Runtime;

/// <summary>
/// The Amiga that the runtime emulates: the video standard, the chipset, and what the CPU can do. The default is an
/// NTSC Amiga with the ECS chipset and a 68000 at 7.16 MHz, as the Prevue machines were.
/// </summary>
/// <param name="Video">NTSC or PAL.</param>
/// <param name="Aga">True for the AGA chipset of the A1200 and the A4000, false for ECS.</param>
/// <param name="UnalignedAccess">True to allow words and longs at odd addresses, as a 68020 does.</param>
/// <param name="CpuClockHz">The speed of the CPU for the pacing, in cycles of the 68000 each second.</param>
public sealed record MachineConfig(VideoStandard Video, bool Aga, bool UnalignedAccess, double CpuClockHz)
{
    /// <summary>An NTSC Amiga with ECS and a 68000 at 7.16 MHz.</summary>
    public static readonly MachineConfig Default = new(VideoStandard.Ntsc, false, false, CycleEstimate.ClockHz);
}
