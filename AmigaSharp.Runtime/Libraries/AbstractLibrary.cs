namespace AmigaSharp.Runtime.Libraries;

public abstract class AbstractLibrary
{
    public abstract string Name { get; }
    public abstract ushort Version { get; }
    public virtual ushort Revision => 0;

    /// <summary>The ID string in lib_IdString, for example "exec 40.10 (15.7.93)".</summary>
    public virtual string IdString => $"{Name.Replace(".library", "")} {Version}.{Revision}";

    /// <summary>The number of bytes above the library base. Override this if the library base is larger than <c>struct Library</c>.</summary>
    public virtual ushort PositiveSize => LibraryOffsets.Size;

    /// <summary>
    /// The offset of the last vector of the real library. The jump table has a vector for each offset from -6 to this
    /// offset. A vector with no native function reports the call with <see cref="MissingLibraryFunctionException"/>.
    /// </summary>
    public virtual short LowestOffset => -30;

    public uint Base { get; internal set; }
    public ushort OpenCount { get; internal set; }

    /// <summary>Called after the library base is in memory. A library can set up the fields above <c>struct Library</c> here.</summary>
    public virtual void Initialize()
    {
    }
}
