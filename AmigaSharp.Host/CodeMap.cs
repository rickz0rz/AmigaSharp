using System.Globalization;
using System.Security.Cryptography;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Loader;

namespace AmigaSharp.Host;

/// <summary>
/// The addresses where the interpreter ran code of a program in earlier runs. The translator also translates the code
/// at these addresses, so each run translates more of a program that has no listing.
/// </summary>
/// <remarks>
/// <para>
/// Without a listing, the translator finds code from the entry point of the program and from the calls that it can see.
/// Code that the program reaches in another way, for example through a table of addresses or an interrupt vector,
/// runs in the interpreter. The launcher writes the addresses where the interpreter started to a file of the program
/// (its SHA-256), and the next translation also starts at them.
/// </para>
/// <para>
/// The map keeps only addresses in the contents of a hunk of the file, where the memory still has the bytes of the
/// file. Code that the program unpacks or writes at run time is not in the file, so it stays in the interpreter.
/// </para>
/// </remarks>
public sealed class CodeMap
{
    private readonly string _path;

    private CodeMap(string path, SortedSet<uint> addresses)
    {
        _path = path;
        Addresses = addresses;
    }

    /// <summary>The addresses of code that ran.</summary>
    public SortedSet<uint> Addresses { get; }

    /// <summary>Reads the map of a program, or makes an empty one.</summary>
    public static CodeMap Load(byte[] executable, string? directory = null)
    {
        directory ??= ProgramCompiler.CacheDirectory;
        var name = Convert.ToHexString(SHA256.HashData(executable))[..24] + ".code";
        var path = Path.Combine(directory, name);
        var addresses = new SortedSet<uint>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (uint.TryParse(line.Trim().TrimStart('$'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
                    addresses.Add(address);
            }
        }

        return new CodeMap(path, addresses);
    }

    /// <summary>
    /// Adds the addresses that are code of the file: in the contents of a hunk, with the bytes of the file in memory.
    /// Writes the map if it changed. Returns the number of new addresses.
    /// </summary>
    public int Add(byte[] executable, Memory memory, IEnumerable<uint> addresses)
    {
        var file = HunkFile.Parse(executable);
        var bases = HunkLayout.Assign(file);
        var added = 0;
        foreach (var address in addresses)
        {
            if (!Addresses.Contains(address) && IsCodeOfFile(file, bases, memory, address) && Addresses.Add(address))
                added++;
        }

        if (added > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, Addresses.Select(address => address.ToString("X6", CultureInfo.InvariantCulture)));
        }

        return added;
    }

    private static bool IsCodeOfFile(HunkFile file, uint[] bases, Memory memory, uint address)
    {
        if ((address & 1) != 0)
            return false;
        foreach (var hunk in file.Hunks)
        {
            var offset = (long)address - bases[hunk.Index];
            if (offset < 0 || offset + 2 > hunk.Data.Length)
                continue;
            // The first bytes of the code must be the bytes of the file. The relocations change only longs of
            // addresses, so the opcode word is the same.
            return memory.Read8(address) == hunk.Data[offset] && memory.Read8(address + 1) == hunk.Data[offset + 1];
        }

        return false;
    }
}
