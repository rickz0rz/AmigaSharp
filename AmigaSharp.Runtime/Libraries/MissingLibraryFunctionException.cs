namespace AmigaSharp.Runtime.Libraries;

/// <summary>The program called a library function that the runtime does not implement.</summary>
public sealed class MissingLibraryFunctionException(string library, int offset)
    : Exception($"{library} has no function at offset {offset} (${-offset:X}).")
{
    public string Library { get; } = library;
    public int Offset { get; } = offset;
}
