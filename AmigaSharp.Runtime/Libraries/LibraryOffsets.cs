namespace AmigaSharp.Runtime.Libraries;

/// <summary>
/// Field offsets in <c>struct Library</c> (exec/libraries.h), from the library base.
/// </summary>
public static class LibraryOffsets
{
    public const uint NodeType = 8;
    public const uint NodeName = 10;
    public const uint Flags = 14;
    public const uint NegativeSize = 16;
    public const uint PositiveSize = 18;
    public const uint Version = 20;
    public const uint Revision = 22;
    public const uint IdString = 24;
    public const uint Sum = 28;
    public const uint OpenCount = 32;

    /// <summary>The size of <c>struct Library</c>.</summary>
    public const ushort Size = 34;

    public const byte NodeTypeLibrary = 9;
}
