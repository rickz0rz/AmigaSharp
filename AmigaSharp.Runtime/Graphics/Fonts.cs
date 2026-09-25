using System.Buffers.Binary;
using System.Text;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Loader;

namespace AmigaSharp.Runtime.Graphics;

/// <summary>
/// Makes the fonts in memory: the built-in "topaz.font" 8 and the fonts that diskfont.library loads from FONTS:.
/// Each font is a real <c>TextFont</c> in the font list of GfxBase.
/// </summary>
public sealed class Fonts(Core core)
{
    /// <summary>The ID of a .font file (FCH_ID) and of a .font file with tags (TFCH_ID).</summary>
    private const ushort ContentsId = 0x0F00;
    private const ushort TaggedContentsId = 0x0F02;

    /// <summary>The ID of the DiskFontHeader in a font file (DFH_ID).</summary>
    private const ushort DiskFontHeaderId = 0x0F80;

    // A font file starts with MOVEQ #-1,D0 / RTS. The DiskFontHeader follows, and the TextFont is at offset 54 in it.
    private const uint DiskFontHeaderOffset = 4;
    private const uint DiskFontHeaderFileId = 14;
    private const uint TextFontInHeader = 54;

    private const int ContentsEntrySize = 260;
    private const int ContentsNameLength = 256;

    private readonly Memory _memory = core.Memory;

    /// <summary>Makes the built-in 8x8 font. Its name is "topaz.font".</summary>
    public uint CreateBuiltInFont()
    {
        const int count = BuiltInFont.LastCharacter - BuiltInFont.FirstCharacter + 1;
        // All glyphs are in one bitmap, next to each other: each row of the bitmap is one row of each glyph.
        const int modulo = count + 1;
        var glyphs = BuiltInFont.Glyphs;
        var charData = core.AllocateSystem((uint)(modulo * BuiltInFont.Height), MemoryFlags.Chip);
        for (var row = 0; row < BuiltInFont.Height; row++)
        {
            for (var character = 0; character < count; character++)
                _memory.Write8(charData + (uint)(row * modulo + character), glyphs[character * BuiltInFont.Height + row]);
        }

        // One location for each character and one for the character that the font does not have. That glyph is
        // the last column: a box.
        for (var row = 0; row < BuiltInFont.Height; row++)
            _memory.Write8(charData + (uint)(row * modulo + count), row is 0 or BuiltInFont.Height - 1 ? (byte)0xFE : (byte)0x82);
        var charLoc = core.AllocateSystem((count + 1) * 4);
        for (var character = 0; character <= count; character++)
            _memory.Write32(charLoc + (uint)character * 4, (uint)(character * BuiltInFont.Width) << 16 | BuiltInFont.Width);

        var font = core.AllocateSystem(TextFontOffsets.Size);
        _memory.Write8(font + NodeOffsets.Type, TextFontOffsets.NodeTypeFont);
        _memory.Write32(font + NodeOffsets.Name, core.AllocateSystem(Encoding.Latin1.GetBytes("topaz.font\0")));
        _memory.Write16(font + MessageOffsets.Length, (ushort)TextFontOffsets.Size);
        _memory.Write16(font + TextFontOffsets.YSize, BuiltInFont.Height);
        _memory.Write8(font + TextFontOffsets.Flags, TextFontOffsets.RomFont);
        _memory.Write16(font + TextFontOffsets.XSize, BuiltInFont.Width);
        _memory.Write16(font + TextFontOffsets.Baseline, BuiltInFont.Baseline);
        _memory.Write16(font + TextFontOffsets.BoldSmear, 1);
        _memory.Write8(font + TextFontOffsets.LoChar, BuiltInFont.FirstCharacter);
        _memory.Write8(font + TextFontOffsets.HiChar, BuiltInFont.LastCharacter);
        _memory.Write32(font + TextFontOffsets.CharData, charData);
        _memory.Write16(font + TextFontOffsets.Modulo, modulo);
        _memory.Write32(font + TextFontOffsets.CharLoc, charLoc);
        return font;
    }

    /// <summary>
    /// Finds the font in a font list with the name and the size. The name does not have to match in case.
    /// Returns 0 if no font has the name. If no font has the size, returns the font that is nearest in size, unless
    /// <paramref name="exactSize"/> is true.
    /// </summary>
    public uint Find(uint fontList, string name, int ySize, bool exactSize)
    {
        uint best = 0;
        var bestDifference = int.MaxValue;
        foreach (var font in ExecList.Nodes(_memory, fontList))
        {
            var fontName = _memory.Read32(font + NodeOffsets.Name);
            if (fontName == 0 || !string.Equals(_memory.ReadCString(fontName), name, StringComparison.OrdinalIgnoreCase))
                continue;
            var difference = Math.Abs(_memory.Read16(font + TextFontOffsets.YSize) - ySize);
            if (difference < bestDifference)
            {
                best = font;
                bestDifference = difference;
            }
        }

        return exactSize && bestDifference != 0 ? 0 : best;
    }

    /// <summary>
    /// Loads a font from FONTS:. For "Prevue.font" and size 20, the contents file is FONTS:Prevue.font and it
    /// names the font file, usually FONTS:Prevue/20. Returns the TextFont, or 0 if the font or the size does not
    /// exist.
    /// </summary>
    public uint LoadDiskFont(string name, int ySize)
    {
        var directory = _memory.Read32(core.MainProcess + ProcessOffsets.CurrentDir);
        var (contentsPath, _) = core.FileSystem.Resolve("FONTS:" + name, directory, mustExist: true);
        if (contentsPath == null)
            return 0;

        var contents = File.ReadAllBytes(contentsPath);
        var id = BinaryPrimitives.ReadUInt16BigEndian(contents);
        if (id is not (ContentsId or TaggedContentsId))
            return 0;

        var entries = BinaryPrimitives.ReadUInt16BigEndian(contents.AsSpan(2));
        string? fileName = null;
        var bestDifference = int.MaxValue;
        for (var i = 0; i < entries && 4 + (i + 1) * ContentsEntrySize <= contents.Length; i++)
        {
            var entry = contents.AsSpan(4 + i * ContentsEntrySize, ContentsEntrySize);
            var entryName = Encoding.Latin1.GetString(entry[..ContentsNameLength]).Split('\0')[0];
            var entrySize = BinaryPrimitives.ReadUInt16BigEndian(entry[ContentsNameLength..]);
            var difference = Math.Abs(entrySize - ySize);
            if (difference < bestDifference)
            {
                fileName = entryName;
                bestDifference = difference;
            }
        }

        if (fileName == null)
            return 0;
        var (fontPath, _) = core.FileSystem.Resolve("FONTS:" + fileName, directory, mustExist: true);
        return fontPath == null ? 0 : LoadFontFile(File.ReadAllBytes(fontPath), name);
    }

    /// <summary>Loads a font file (a hunk file with a DiskFontHeader) into chip memory. Returns the TextFont.</summary>
    public uint LoadFontFile(byte[] bytes, string name)
    {
        var file = HunkFile.Parse(bytes);
        var bases = new uint[file.Hunks.Count];
        foreach (var hunk in file.Hunks)
            bases[hunk.Index] = core.AllocateSystem(hunk.Size, MemoryFlags.Chip);
        file.Load(_memory, bases);

        var header = bases[0] + DiskFontHeaderOffset;
        if (_memory.Read16(header + DiskFontHeaderFileId) != DiskFontHeaderId)
            throw new InvalidDataException($"{name} is not a font file: the DiskFontHeader is missing.");

        var font = header + TextFontInHeader;
        _memory.Write8(font + NodeOffsets.Type, TextFontOffsets.NodeTypeFont);
        _memory.Write32(font + NodeOffsets.Name, core.AllocateSystem(Encoding.Latin1.GetBytes(name + "\0")));
        _memory.Write8(font + TextFontOffsets.Flags, (byte)(_memory.Read8(font + TextFontOffsets.Flags) | TextFontOffsets.DiskFont));
        _memory.Write16(font + TextFontOffsets.Accessors, 0);
        return font;
    }
}
