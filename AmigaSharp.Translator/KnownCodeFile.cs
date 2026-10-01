using System.Globalization;

namespace AmigaSharp.Translator;

/// <summary>
/// A file of addresses of known code: one hexadecimal address on each line, for example 22DC06. The launcher writes
/// it from the code that ran in the interpreter, and the translator also translates the code at these addresses. Lines
/// that are not an address are left out, so a file can have comments.
/// </summary>
public static class KnownCodeFile
{
    /// <summary>Reads the addresses of a file. A file that does not exist has no addresses.</summary>
    public static SortedSet<uint> Read(string path)
    {
        var addresses = new SortedSet<uint>();
        if (!File.Exists(path))
            return addresses;
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim().TrimStart('$');
            if (uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
                addresses.Add(address);
        }

        return addresses;
    }

    /// <summary>Writes the addresses, in order, one on each line.</summary>
    public static void Write(string path, IEnumerable<uint> addresses)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);
        File.WriteAllLines(path, addresses.Order().Distinct()
            .Select(address => address.ToString("X6", CultureInfo.InvariantCulture)));
    }

    /// <summary>Writes the addresses of all the input files to the output file. Returns the number of addresses.</summary>
    public static int Merge(string output, IEnumerable<string> inputs)
    {
        var addresses = new SortedSet<uint>();
        foreach (var input in inputs)
        {
            if (!File.Exists(input))
                throw new FileNotFoundException($"The map {input} does not exist.", input);
            addresses.UnionWith(Read(input));
        }

        Write(output, addresses);
        return addresses.Count;
    }
}
