namespace AmigaSharp.Runtime.Libraries;

public abstract class AbstractLibrary
{
    public abstract string Name { get; }
    public abstract ushort Version { get; }
    public virtual ushort Revision => 0;

    /// <summary>The number of bytes above the library base. Override this if the library base is larger than <c>struct Library</c>.</summary>
    public virtual ushort PositiveSize => LibraryOffsets.Size;

    public uint Base { get; internal set; }
    public ushort OpenCount { get; internal set; }
}
