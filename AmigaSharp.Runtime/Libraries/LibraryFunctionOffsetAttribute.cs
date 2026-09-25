namespace AmigaSharp.Runtime.Libraries;

/// <summary>
/// The library vector offset (LVO) of a library function. The offset is negative.
/// </summary>
/// <remarks>
/// The method can read the registers itself, or it can have parameters with a register attribute such as
/// <see cref="A1Attribute"/>. A return value of type <see cref="uint"/> or <see cref="int"/> goes to D0.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LibraryFunctionOffsetAttribute(short offset) : Attribute
{
    public short Offset { get; } = offset;
}
