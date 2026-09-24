using System.Buffers.Binary;
using System.Text;

namespace AmigaSharp.Runtime.Loader;

public enum HunkType : byte
{
    Code,
    Data,
    Bss,
}

/// <summary>The memory type that a hunk asks for in its header.</summary>
public enum HunkMemory : byte
{
    Any,
    Chip,
    Fast,
}

/// <summary>A 32-bit relocation: at <see cref="Offset"/>, add the base address of hunk <see cref="TargetHunk"/>.</summary>
public readonly record struct Relocation(uint Offset, int TargetHunk);

public sealed class Hunk
{
    public required int Index { get; init; }
    public required HunkType Type { get; init; }
    public required HunkMemory Memory { get; init; }

    /// <summary>The number of bytes to allocate. This can be more than the length of <see cref="Data"/>.</summary>
    public required uint Size { get; init; }

    /// <summary>The initial contents. A BSS hunk has no data. The loader fills the rest of the hunk with zeros.</summary>
    public required byte[] Data { get; init; }

    public required IReadOnlyList<Relocation> Relocations { get; init; }
}

/// <summary>
/// An AmigaOS executable in the hunk format, as LoadSeg reads it.
/// </summary>
public sealed class HunkFile
{
    private const uint HunkCode = 0x3E9;
    private const uint HunkData = 0x3EA;
    private const uint HunkBss = 0x3EB;
    private const uint HunkReloc32 = 0x3EC;
    private const uint HunkSymbol = 0x3F0;
    private const uint HunkDebug = 0x3F1;
    private const uint HunkEnd = 0x3F2;
    private const uint HunkHeader = 0x3F3;

    // LoadSeg reads HUNK_DREL32 in an executable as a short relocation block, the same as HUNK_RELOC32SHORT.
    private const uint HunkDrel32 = 0x3F7;
    private const uint HunkReloc32Short = 0x3FC;

    public required IReadOnlyList<Hunk> Hunks { get; init; }

    public static HunkFile Read(string path) => Parse(File.ReadAllBytes(path));

    public static HunkFile Parse(byte[] bytes)
    {
        var reader = new Reader(bytes);
        if (reader.Long() != HunkHeader)
            throw new InvalidDataException("The file is not an AmigaOS executable: HUNK_HEADER is missing.");

        // Skip the names of resident libraries. The list ends with a zero length.
        for (var length = reader.Long(); length != 0; length = reader.Long())
            reader.Skip(length * 4);

        reader.Long(); // The table size.
        var first = (int)reader.Long();
        var last = (int)reader.Long();
        var sizes = new List<(uint Size, HunkMemory Memory)>();
        for (var i = first; i <= last; i++)
        {
            var value = reader.Long();
            var memory = (value >> 30) switch
            {
                1 => HunkMemory.Chip,
                2 => HunkMemory.Fast,
                _ => HunkMemory.Any,
            };
            // If both memory bits are set, the next long gives the memory attributes.
            if (value >> 30 == 3)
                reader.Long();
            sizes.Add(((value & 0x3FFF_FFFF) * 4, memory));
        }

        var hunks = new List<Hunk>();
        for (var index = 0; index < sizes.Count; index++)
            hunks.Add(ReadHunk(ref reader, index, sizes[index].Size, sizes[index].Memory));
        return new HunkFile { Hunks = hunks };
    }

    /// <summary>Copies the hunks to memory at the base addresses and applies the relocations.</summary>
    public void Load(Memory memory, IReadOnlyList<uint> bases)
    {
        foreach (var hunk in Hunks)
        {
            var baseAddress = bases[hunk.Index];
            for (uint i = 0; i < hunk.Size; i++)
                memory.Write8(baseAddress + i, i < hunk.Data.Length ? hunk.Data[i] : (byte)0);
        }

        foreach (var hunk in Hunks)
        {
            foreach (var relocation in hunk.Relocations)
            {
                var address = bases[hunk.Index] + relocation.Offset;
                memory.Write32(address, memory.Read32(address) + bases[relocation.TargetHunk]);
            }
        }
    }

    private static Hunk ReadHunk(ref Reader reader, int index, uint size, HunkMemory memory)
    {
        HunkType? type = null;
        var data = Array.Empty<byte>();
        var relocations = new List<Relocation>();
        while (true)
        {
            var id = reader.Long() & 0x3FFF_FFFF;
            switch (id)
            {
                case HunkCode or HunkData:
                    type = id == HunkCode ? HunkType.Code : HunkType.Data;
                    data = reader.Bytes(reader.Long() * 4);
                    break;
                case HunkBss:
                    type = HunkType.Bss;
                    reader.Long();
                    break;
                case HunkReloc32:
                    for (var count = reader.Long(); count != 0; count = reader.Long())
                    {
                        var target = (int)reader.Long();
                        for (var i = 0; i < count; i++)
                            relocations.Add(new Relocation(reader.Long(), target));
                    }

                    break;
                case HunkReloc32Short or HunkDrel32:
                    for (uint count = reader.Word(); count != 0; count = reader.Word())
                    {
                        var target = (int)reader.Word();
                        for (var i = 0; i < count; i++)
                            relocations.Add(new Relocation(reader.Word(), target));
                    }

                    reader.AlignToLong();
                    break;
                case HunkSymbol:
                    for (var length = reader.Long(); length != 0; length = reader.Long())
                        reader.Skip((length & 0x00FF_FFFF) * 4 + 4);
                    break;
                case HunkDebug:
                    reader.Skip(reader.Long() * 4);
                    break;
                case HunkEnd:
                    if (type == null)
                        throw new InvalidDataException($"Hunk {index} has no CODE, DATA or BSS block.");
                    return new Hunk
                    {
                        Index = index, Type = type.Value, Memory = memory, Size = Math.Max(size, (uint)data.Length),
                        Data = data, Relocations = relocations,
                    };
                default:
                    throw new InvalidDataException($"Hunk {index} has an unknown block type ${id:X}.");
            }
        }
    }

    private ref struct Reader(byte[] bytes)
    {
        private int _position;

        public uint Long()
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(_position, 4));
            _position += 4;
            return value;
        }

        public ushort Word()
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(_position, 2));
            _position += 2;
            return value;
        }

        public byte[] Bytes(uint count)
        {
            var value = bytes.AsSpan(_position, (int)count).ToArray();
            _position += (int)count;
            return value;
        }

        public void Skip(uint count) => _position += (int)count;

        public void AlignToLong() => _position = (_position + 3) & ~3;
    }
}

/// <summary>
/// Chooses the load addresses of the hunks. The translator and the runtime must use the same addresses, because the
/// translated code contains the relocated addresses as constants.
/// </summary>
public static class HunkLayout
{
    /// <summary>Chip hunks load here, in chip RAM. The runtime allocator uses the memory below this address.</summary>
    public const uint ChipBase = 0x10_0000;

    /// <summary>The other hunks load here, in the first fast RAM area of the Zorro II space.</summary>
    public const uint FastBase = 0x20_0000;

    public static uint[] Assign(HunkFile file)
    {
        var bases = new uint[file.Hunks.Count];
        var chip = ChipBase;
        var fast = FastBase;
        foreach (var hunk in file.Hunks)
        {
            ref var next = ref hunk.Memory == HunkMemory.Chip ? ref chip : ref fast;
            bases[hunk.Index] = next;
            next = (next + hunk.Size + 7) & ~7u;
        }

        return bases;
    }

    public static string Describe(HunkFile file, IReadOnlyList<uint> bases)
    {
        var text = new StringBuilder();
        foreach (var hunk in file.Hunks)
            text.AppendLine($"hunk {hunk.Index}: {hunk.Type} {hunk.Memory} ${bases[hunk.Index]:X6} size {hunk.Size}");
        return text.ToString();
    }
}
