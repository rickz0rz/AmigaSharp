namespace AmigaSharp.Runtime;

/// <summary>
/// A word or long access used an odd address. A real 68000 raises an address error exception for this.
/// </summary>
public sealed class AddressErrorException(uint address)
    : Exception($"Address error: word or long access at odd address ${address:X6}.")
{
    public uint Address { get; } = address;
}
