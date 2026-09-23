namespace AmigaSharp.Runtime.Libraries;

/// <summary>The library vector offset (LVO) of a library function. The offset is negative.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LibraryFunctionOffsetAttribute(short offset) : Attribute
{
    public short Offset { get; } = offset;
}
