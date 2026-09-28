namespace AmigaSharp.Runtime.Exec;

/// <summary>The memory requirements of AllocMem (exec/memory.h).</summary>
[Flags]
public enum MemoryFlags : uint
{
    Any = 0,
    Public = 1 << 0,
    Chip = 1 << 1,
    Fast = 1 << 2,
    Local = 1 << 8,
    Dma24Bit = 1 << 9,
    Kick = 1 << 10,
    Clear = 1 << 16,
    Largest = 1 << 17,
    Reverse = 1 << 18,
    Total = 1 << 19,
}

/// <summary>
/// The free memory of the system, as exec keeps it in its memory lists. Each allocation is a multiple of 8 bytes and
/// starts at an address that is a multiple of 8.
/// </summary>
public sealed class MemoryAllocator(Memory memory)
{
    /// <summary>The allocation unit of exec (MEM_BLOCKSIZE).</summary>
    public const uint BlockSize = 8;

    private readonly List<Region> _regions = [];

    // The emulation changes the free lists, and another thread can read them, for example to show the free memory.
    private readonly Lock _lock = new();

    private sealed class Region(uint start, uint end, bool isChip)
    {
        public uint Start { get; } = start;
        public uint End { get; } = end;
        public bool IsChip { get; } = isChip;

        /// <summary>The free blocks: start address to size. The blocks do not touch or overlap.</summary>
        public SortedList<uint, uint> Free { get; } = new() { { start, end - start } };
    }

    /// <summary>Adds a range of memory. Chip memory is the memory that the custom chips can access.</summary>
    public void AddRegion(uint start, uint end, bool isChip)
    {
        _regions.Add(new Region(RoundUp(start), end & ~(BlockSize - 1), isChip));
    }

    /// <summary>Removes a range from the free memory, for example the memory of a loaded hunk.</summary>
    public void Reserve(uint start, uint size)
    {
        lock (_lock)
            ReserveLocked(start, size);
    }

    private void ReserveLocked(uint start, uint size)
    {
        var first = start & ~(BlockSize - 1);
        var end = RoundUp(start + size);
        var region = RegionOf(first) ?? throw new ArgumentOutOfRangeException(nameof(start), $"${start:X6} is not in a memory region.");
        foreach (var (blockStart, blockSize) in region.Free.ToList())
        {
            var blockEnd = blockStart + blockSize;
            if (blockEnd <= first || blockStart >= end)
                continue;

            region.Free.Remove(blockStart);
            if (blockStart < first)
                region.Free.Add(blockStart, first - blockStart);
            if (blockEnd > end)
                region.Free.Add(end, blockEnd - end);
        }
    }

    /// <summary>AllocMem: returns the address of the memory, or 0 if no block is large enough.</summary>
    public uint Allocate(uint size, MemoryFlags flags)
    {
        lock (_lock)
            return AllocateLocked(size, flags);
    }

    private uint AllocateLocked(uint size, MemoryFlags flags)
    {
        if (size == 0)
            return 0;

        size = RoundUp(size);
        foreach (var region in CandidateRegions(flags))
        {
            var reverse = flags.HasFlag(MemoryFlags.Reverse);
            var blocks = reverse ? region.Free.Reverse() : region.Free;
            foreach (var (blockStart, blockSize) in blocks)
            {
                if (blockSize < size)
                    continue;

                region.Free.Remove(blockStart);
                uint address;
                if (reverse)
                {
                    address = blockStart + blockSize - size;
                    if (blockSize > size)
                        region.Free.Add(blockStart, blockSize - size);
                }
                else
                {
                    address = blockStart;
                    if (blockSize > size)
                        region.Free.Add(blockStart + size, blockSize - size);
                }

                if (flags.HasFlag(MemoryFlags.Clear))
                    Clear(address, size);
                return address;
            }
        }

        return 0;
    }

    /// <summary>FreeMem: returns the memory to the free lists.</summary>
    /// <exception cref="InvalidOperationException">The memory is already free, or it is not in a region.</exception>
    public void Free(uint address, uint size)
    {
        lock (_lock)
            FreeLocked(address, size);
    }

    private void FreeLocked(uint address, uint size)
    {
        // Cleanup code often frees a null pointer. Exec does nothing for it.
        if (size == 0 || address == 0)
            return;

        var start = address & ~(BlockSize - 1);
        var end = RoundUp(address + size);
        var region = RegionOf(start) ?? throw new InvalidOperationException($"FreeMem: ${address:X6} is not in a memory region.");
        var free = region.Free;

        var index = LowerBound(free, start);
        // The block before must end at or before the start, and the next block must start at or after the end.
        if (index > 0 && free.Keys[index - 1] + free.Values[index - 1] > start
            || index < free.Count && free.Keys[index] < end)
            throw new InvalidOperationException($"FreeMem: ${address:X6} ({size} bytes) is already free.");

        var newStart = start;
        var newEnd = end;
        if (index > 0 && free.Keys[index - 1] + free.Values[index - 1] == start)
        {
            newStart = free.Keys[index - 1];
            free.RemoveAt(index - 1);
            index--;
        }

        if (index < free.Count && free.Keys[index] == end)
        {
            newEnd = end + free.Values[index];
            free.RemoveAt(index);
        }

        free.Add(newStart, newEnd - newStart);
    }

    /// <summary>AvailMem: the free memory, the largest free block (MEMF_LARGEST), or all memory (MEMF_TOTAL).</summary>
    public uint Available(MemoryFlags flags)
    {
        lock (_lock)
            return AvailableLocked(flags);
    }

    private uint AvailableLocked(MemoryFlags flags)
    {
        var regions = CandidateRegions(flags).ToList();
        if (flags.HasFlag(MemoryFlags.Total))
            return (uint)regions.Sum(region => region.End - region.Start);
        if (flags.HasFlag(MemoryFlags.Largest))
            return regions.SelectMany(region => region.Free.Values).DefaultIfEmpty(0u).Max();
        return (uint)regions.SelectMany(region => region.Free.Values).Sum(value => (long)value);
    }

    /// <summary>TypeOfMem: the attributes of the memory at the address, or 0 if the address is not in a region.</summary>
    public MemoryFlags TypeOf(uint address)
    {
        var region = RegionOf(address);
        if (region == null)
            return 0;
        return MemoryFlags.Public | (region.IsChip ? MemoryFlags.Chip | MemoryFlags.Dma24Bit : MemoryFlags.Fast | MemoryFlags.Dma24Bit);
    }

    private IEnumerable<Region> CandidateRegions(MemoryFlags flags)
    {
        if (flags.HasFlag(MemoryFlags.Chip))
            return _regions.Where(region => region.IsChip);
        if (flags.HasFlag(MemoryFlags.Fast))
            return _regions.Where(region => !region.IsChip);
        // Exec gives fast memory first, because only the custom chips need chip memory.
        return _regions.Where(region => !region.IsChip).Concat(_regions.Where(region => region.IsChip));
    }

    private Region? RegionOf(uint address) =>
        _regions.FirstOrDefault(region => address >= region.Start && address < region.End);

    private void Clear(uint address, uint size)
    {
        for (uint i = 0; i < size; i += 4)
            memory.Write32(address + i, 0);
    }

    private static uint RoundUp(uint value) => (value + BlockSize - 1) & ~(BlockSize - 1);

    private static int LowerBound(SortedList<uint, uint> list, uint key)
    {
        int low = 0, high = list.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (list.Keys[middle] < key)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}
