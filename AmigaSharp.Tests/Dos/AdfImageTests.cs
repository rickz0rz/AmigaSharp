using System.Buffers.Binary;
using System.Text;
using AmigaSharp.Runtime.Dos;

namespace AmigaSharp.Tests.Dos;

public class AdfImageTests
{
    [Fact]
    public void Read_OfsDisk_ReadsTheFilesAndTheDirectories()
    {
        var disk = new DiskBuilder(fastFileSystem: false);
        var text = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        disk.AddFile(disk.Root, 0, "Program", text);
        var directory = disk.AddDirectory(disk.Root, 5, "s");
        disk.AddFile(directory, 3, "Startup-Sequence", "Program\n"u8.ToArray());

        var image = AdfImage.Read(disk.Image);

        Assert.Equal("Test Disk", image.VolumeName);
        Assert.False(image.FastFileSystem);
        Assert.Equal(["Program", "s", "s/Startup-Sequence"], image.Entries.Select(e => e.Path));
        Assert.Equal(text, image.Entries[0].Data);
        Assert.True(image.Entries[1].IsDirectory);
        Assert.Equal("Program\n"u8.ToArray(), image.Entries[2].Data);
    }

    [Fact]
    public void Read_FfsFileWithAnExtensionBlock_ReadsAllDataBlocks()
    {
        var disk = new DiskBuilder(fastFileSystem: true);
        // 73 blocks: the file header lists 72 data blocks, and an extension block lists the last one.
        var data = Enumerable.Range(0, 73 * 512 - 100).Select(i => (byte)(i * 7)).ToArray();
        disk.AddFile(disk.Root, 10, "Big", data);

        var image = AdfImage.Read(disk.Image);

        Assert.True(image.FastFileSystem);
        Assert.Equal(data, Assert.Single(image.Entries).Data);
    }

    [Fact]
    public void Read_DiskWithADamagedFile_GivesTheOtherFiles_AndNamesTheDamagedOne()
    {
        var disk = new DiskBuilder(fastFileSystem: false);
        disk.AddFile(disk.Root, 0, "Good", "good"u8.ToArray()); // Header block 2, data block 3.
        disk.AddFile(disk.Root, 1, "Bad", "bad"u8.ToArray()); // Header block 4, data block 5.
        // The data block of Bad is another kind of block: its size field is larger than a block.
        BinaryPrimitives.WriteUInt32BigEndian(disk.Image.AsSpan(5 * 512 + 12), 1260);

        var image = AdfImage.Read(disk.Image);

        Assert.Equal(["Good"], image.Entries.Select(e => e.Path));
        var (path, reason) = Assert.Single(image.DamagedFiles);
        Assert.Equal("Bad", path);
        Assert.Contains("block 5", reason);
    }

    [Fact]
    public void Read_ImageWithoutDos_Throws()
    {
        Assert.Throws<InvalidDataException>(() => AdfImage.Read(new byte[901_120]));
    }

    /// <summary>Makes a small AmigaDOS disk image. It does not compute the hashes or the checksums: the reader does not check them.</summary>
    private sealed class DiskBuilder
    {
        private const int BlockSize = 512;
        private readonly bool _fastFileSystem;
        private int _nextBlock = 2;

        public DiskBuilder(bool fastFileSystem)
        {
            _fastFileSystem = fastFileSystem;
            Encoding.ASCII.GetBytes("DOS").CopyTo(Image, 0);
            Image[3] = (byte)(fastFileSystem ? 1 : 0);
            Long(Root, 0, 2);
            Long(Root, 12, 72);
            Long(Root, BlockSize - 4, 1);
            Name(Root, "Test Disk");
        }

        public byte[] Image { get; } = new byte[1760 * BlockSize];
        public int Root => 880;

        public int AddDirectory(int parent, int slot, string name)
        {
            var block = Allocate();
            Long(block, 0, 2);
            Long(block, BlockSize - 4, 2);
            Name(block, name);
            Link(parent, slot, block);
            return block;
        }

        public void AddFile(int parent, int slot, string name, byte[] data)
        {
            var header = Allocate();
            Long(header, 0, 2);
            Long(header, BlockSize - 4, unchecked((uint)-3));
            Long(header, BlockSize - 188, (uint)data.Length);
            Name(header, name);
            Link(parent, slot, header);

            var payload = _fastFileSystem ? BlockSize : BlockSize - 24;
            var list = header;
            var inList = 0;
            for (var offset = 0; offset < data.Length; offset += payload)
            {
                if (inList == 72)
                {
                    var extension = Allocate();
                    Long(extension, 0, 16);
                    Long(extension, BlockSize - 4, unchecked((uint)-3));
                    Long(list, BlockSize - 8, (uint)extension);
                    list = extension;
                    inList = 0;
                }

                var block = Allocate();
                var chunk = data.AsSpan(offset, Math.Min(payload, data.Length - offset));
                if (_fastFileSystem)
                {
                    chunk.CopyTo(Image.AsSpan(block * BlockSize));
                }
                else
                {
                    Long(block, 0, 8);
                    Long(block, 12, (uint)chunk.Length);
                    chunk.CopyTo(Image.AsSpan(block * BlockSize + 24));
                }

                Long(list, BlockSize - 204 - inList * 4, (uint)block);
                inList++;
                Long(list, 8, (uint)inList);
            }
        }

        private void Link(int parent, int slot, int block)
        {
            var existing = ReadLong(parent, 24 + slot * 4);
            Long(block, BlockSize - 16, existing);
            Long(parent, 24 + slot * 4, (uint)block);
        }

        // The tests use fewer than 100 blocks, so the blocks never reach the root block.
        private int Allocate() => _nextBlock++;

        private void Name(int block, string name)
        {
            Image[block * BlockSize + BlockSize - 80] = (byte)name.Length;
            Encoding.Latin1.GetBytes(name).CopyTo(Image, block * BlockSize + BlockSize - 79);
        }

        private void Long(int block, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(Image.AsSpan(block * BlockSize + offset), value);

        private uint ReadLong(int block, int offset) =>
            BinaryPrimitives.ReadUInt32BigEndian(Image.AsSpan(block * BlockSize + offset));
    }
}
