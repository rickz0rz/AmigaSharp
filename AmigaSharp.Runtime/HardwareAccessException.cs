namespace AmigaSharp.Runtime;

/// <summary>
/// The program accessed a hardware address that the runtime does not model: a register of the custom chips that the
/// model does not have, or a hardware address when no hardware is connected.
/// </summary>
public sealed class HardwareAccessException(uint address)
    : Exception($"Hardware access at ${address:X6}: the runtime has no model for this address.")
{
    public uint Address { get; } = address;
}
