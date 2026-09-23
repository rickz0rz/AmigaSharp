namespace AmigaSharp.Runtime;

/// <summary>
/// The program accessed the custom chips or the CIAs directly. The HLE runtime does not emulate the chipset.
/// </summary>
public sealed class HardwareAccessException(uint address)
    : Exception($"Direct hardware access at ${address:X6}. The runtime does not emulate the chipset.")
{
    public uint Address { get; } = address;
}
