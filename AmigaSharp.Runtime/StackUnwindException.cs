namespace AmigaSharp.Runtime;

/// <summary>
/// The 68000 code returned to an address that is not the return address of the current call. For example, an exit
/// routine can restore a saved stack pointer and return to the caller of the program. The exception moves up the C#
/// call stack to the call that expects this return address. That call then continues normally.
/// </summary>
public sealed class StackUnwindException(uint returnAddress)
    : Exception($"The code returned to ${returnAddress:X6}, but no active call expects that address.")
{
    public uint ReturnAddress { get; } = returnAddress;
}
