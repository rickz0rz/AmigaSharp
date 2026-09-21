namespace AmigaSharp.AmigaCore.Libraries;

public class LibraryFunctionOffsetAttribute(int offset) : Attribute
{
    public int Offset { get; } = offset;
}
