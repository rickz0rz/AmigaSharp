using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Loader;

namespace AmigaSharp.Runtime.Dos;

/// <summary>
/// Loads an executable while a program runs, as dos LoadSeg does: each hunk goes to memory from the allocator, with the
/// relocations, and the segments make a segment list. The interpreter runs the code. The executable that the launcher
/// starts loads at fixed addresses instead, for its translation (see <see cref="HunkLayout"/>).
/// </summary>
public static class SegmentLoader
{
    /// <summary>Loads the executable. Returns the segment list (a BPTR to the first segment), or 0.</summary>
    /// <exception cref="InvalidDataException">The file is not an executable.</exception>
    public static uint Load(Core core, byte[] executable)
    {
        var file = HunkFile.Parse(executable);
        var bases = new uint[file.Hunks.Count];
        foreach (var hunk in file.Hunks)
        {
            var flags = (hunk.Memory == HunkMemory.Chip ? MemoryFlags.Chip : MemoryFlags.Any) | MemoryFlags.Public;
            var segment = core.Allocator.Allocate(hunk.Size + HunkLayout.SegmentHeaderSize, flags);
            if (segment == 0)
            {
                for (var i = 0; i < hunk.Index; i++)
                    core.Allocator.Free(bases[i] - HunkLayout.SegmentHeaderSize, file.Hunks[i].Size + HunkLayout.SegmentHeaderSize);
                return 0;
            }

            bases[hunk.Index] = segment + HunkLayout.SegmentHeaderSize;
        }

        file.Load(core.Memory, bases);
        return file.WriteSegmentList(core.Memory, bases);
    }

    /// <summary>Frees the segments of a segment list, as dos UnLoadSeg does.</summary>
    public static void Unload(Core core, uint segmentList)
    {
        while (segmentList != 0)
        {
            var next = segmentList << 2;
            var start = next - 4;
            var size = core.Memory.Read32(start);
            segmentList = core.Memory.Read32(next);
            core.Allocator.Free(start, size);
        }
    }
}
