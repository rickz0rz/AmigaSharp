namespace AmigaSharp.Runtime.Cpu;

/// <summary>The exception vector numbers of the 68000.</summary>
public static class ExceptionVector
{
    public const int AddressError = 3;
    public const int IllegalInstruction = 4;
    public const int ZeroDivide = 5;
    public const int Chk = 6;
    public const int Trapv = 7;
    public const int PrivilegeViolation = 8;
    public const int Trace = 9;
    public const int LineA = 10;
    public const int LineF = 11;

    /// <summary>TRAP #n uses vector 32 + n.</summary>
    public const int Trap0 = 32;
}

/// <summary>
/// An instruction caused a 68000 exception, for example a division by zero or a privilege violation.
/// The interpreter catches it and does the exception processing.
/// </summary>
public sealed class CpuTrapException(int vector) : Exception($"68000 exception, vector {vector}.")
{
    public int Vector { get; } = vector;

    /// <summary>
    /// True if the stacked PC is the address of the instruction that caused the exception.
    /// False if it is the address of the next instruction.
    /// </summary>
    public bool StacksInstructionAddress => Vector is ExceptionVector.IllegalInstruction
        or ExceptionVector.PrivilegeViolation or ExceptionVector.LineA or ExceptionVector.LineF;
}
