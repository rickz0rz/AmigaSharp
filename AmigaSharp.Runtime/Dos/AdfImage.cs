using System.Buffers.Binary;
using System.Text;

namespace AmigaSharp.Runtime.Dos;

/// <summary>A file or a directory on an Amiga disk. <see cref="Path"/> uses '/' between the names.</summary>
public sealed record AdfEntry(string Path, bool IsDirectory, byte[] Data);

/// <summary>
/// Reads the files of an ADF: the image of an Amiga floppy disk with the original (OFS) or the fast (FFS) file system.
/// </summary>
/// <remarks>
/// The disk has 512-byte blocks. Block 0 and 1 are the boot block: "DOS" and a flags byte (bit 0 set = FFS). The root
/// block is in the middle of the disk. A root block or a directory block has a hash table of 72 entries, and the
/// entries with the same hash are a chain. A file header block lists the data blocks from the end of the block
/// backwards, and an extension block continues the list. An OFS data block has a 24-byte header before the data.
/// </remarks>
public sealed class AdfImage
{
    private const int BlockSize = 512;
    private const int HashTableSize = 72;
    private const int HashTableOffset = 24;
    private const int SecondaryTypeOffset = BlockSize - 4;
    private const int HashChainOffset = BlockSize - 16;
    private const int ExtensionOffset = BlockSize - 8;
    private const int NameOffset = BlockSize - 80;
    private const int FileSizeOffset = BlockSize - 188;
    private const int BlockCountOffset = 8;

    // The table of data blocks fills from its end: the first data block of the file is the last entry.
    private const int FirstDataBlockOffset = BlockSize - 204;
    private const int OfsDataHeaderSize = 24;
    private const int OfsDataSizeOffset = 12;
    private const int SecondaryTypeDirectory = 2;
    private const int SecondaryTypeFile = -3;

    private readonly byte[] _image;

    public string VolumeName { get; }
    public bool FastFileSystem { get; }
    public IReadOnlyList<AdfEntry> Entries { get; }

    /// <summary>
    /// The files that the disk cannot give, with the reason: for example a data block that is not a data block. The
    /// other files are in <see cref="Entries"/>. AmigaDOS also reads such a disk, and gives an error only for a damaged
    /// file.
    /// </summary>
    public IReadOnlyList<(string Path, string Reason)> DamagedFiles => _damaged;

    private readonly List<(string Path, string Reason)> _damaged = [];

    private AdfImage(byte[] image)
    {
        _image = image;
        if (image.Length < 2 * BlockSize || image.Length % BlockSize != 0)
            throw new InvalidDataException("The file is not an ADF: its size is not a multiple of 512 bytes.");
        if (image[0] != 'D' || image[1] != 'O' || image[2] != 'S')
            throw new InvalidDataException("The disk is not an AmigaDOS disk: the boot block does not start with DOS.");

        FastFileSystem = (image[3] & 1) != 0;
        var root = image.Length / BlockSize / 2;
        VolumeName = Name(root);
        var entries = new List<AdfEntry>();
        ReadDirectory(root, "", entries, [root]);
        Entries = entries;
    }

    /// <exception cref="InvalidDataException">The file is not an ADF with an AmigaDOS file system, or it is damaged.</exception>
    public static AdfImage Read(byte[] image) => new(image);

    public static bool IsAdf(string path) => path.EndsWith(".adf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes the files and the directories of the disk to the host directory.</summary>
    public void ExtractTo(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var entry in Entries)
        {
            var path = System.IO.Path.Combine([directory, .. entry.Path.Split('/')]);
            if (entry.IsDirectory)
                Directory.CreateDirectory(path);
            else
                File.WriteAllBytes(path, entry.Data);
        }
    }

    private void ReadDirectory(int block, string path, List<AdfEntry> entries, HashSet<int> visited)
    {
        for (var i = 0; i < HashTableSize; i++)
        {
            for (var entry = Long(block, HashTableOffset + i * 4); entry != 0; entry = Long(entry, HashChainOffset))
            {
                if (!visited.Add(entry))
                    throw new InvalidDataException($"The disk is damaged: block {entry} is in a loop.");

                var name = path + Name(entry);
                switch (Long(entry, SecondaryTypeOffset))
                {
                    case SecondaryTypeDirectory:
                        entries.Add(new AdfEntry(name, true, []));
                        ReadDirectory(entry, name + "/", entries, visited);
                        break;
                    case SecondaryTypeFile:
                        try
                        {
                            entries.Add(new AdfEntry(name, false, ReadFile(entry)));
                        }
                        catch (InvalidDataException e)
                        {
                            _damaged.Add((name, e.Message));
                        }

                        break;
                    default:
                        throw new InvalidDataException($"The disk has an entry of an unknown type: {name}.");
                }
            }
        }
    }

    private byte[] ReadFile(int header)
    {
        var size = (int)Long(header, FileSizeOffset);
        var data = new MemoryStream(size);
        for (var block = header; block != 0 && data.Length < size; block = Long(block, ExtensionOffset))
        {
            var count = Long(block, BlockCountOffset);
            for (var i = 0; i < count && data.Length < size; i++)
            {
                data.Write(DataOf(Long(block, FirstDataBlockOffset - i * 4)));
            }
        }

        if (data.Length < size)
            throw new InvalidDataException("The disk is damaged: a file has fewer data blocks than its size.");
        return data.GetBuffer().AsSpan(0, size).ToArray();
    }

    private ReadOnlySpan<byte> DataOf(int block)
    {
        var bytes = Block(block);
        if (FastFileSystem)
            return bytes;
        var size = (uint)Long(block, OfsDataSizeOffset);
        if (size > BlockSize - OfsDataHeaderSize)
            throw new InvalidDataException($"The disk is damaged: block {block} is not a data block of the file.");
        return bytes.Slice(OfsDataHeaderSize, (int)size);
    }

    private string Name(int block)
    {
        var bytes = Block(block);
        var length = Math.Min(bytes[NameOffset], (byte)30);
        return Encoding.Latin1.GetString(bytes.Slice(NameOffset + 1, length));
    }

    private int Long(int block, int offset) => (int)BinaryPrimitives.ReadUInt32BigEndian(Block(block)[offset..]);

    private ReadOnlySpan<byte> Block(int block)
    {
        if (block <= 0 || (block + 1) * BlockSize > _image.Length)
            throw new InvalidDataException($"The disk is damaged: block {block} is not on the disk.");
        return _image.AsSpan(block * BlockSize, BlockSize);
    }
}
