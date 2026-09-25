using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Exec;

namespace AmigaSharp.Tests.Exec;

public class MemoryAllocatorTests
{
    private readonly Memory _memory = new();
    private readonly MemoryAllocator _allocator;

    public MemoryAllocatorTests()
    {
        _allocator = new MemoryAllocator(_memory);
        _allocator.AddRegion(0x1000, 0x2000, isChip: true);
        _allocator.AddRegion(0x10000, 0x11000, isChip: false);
    }

    [Fact]
    public void Allocate_Any_PrefersFastMemory()
    {
        Assert.Equal(0x10000u, _allocator.Allocate(10, MemoryFlags.Any));
        Assert.Equal(0x1000u, _allocator.Allocate(10, MemoryFlags.Chip));
    }

    [Fact]
    public void Allocate_RoundsToEightBytes()
    {
        var first = _allocator.Allocate(1, MemoryFlags.Fast);
        var second = _allocator.Allocate(1, MemoryFlags.Fast);

        Assert.Equal(first + 8, second);
    }

    [Fact]
    public void Allocate_Reverse_TakesTheTopOfTheRegion()
    {
        Assert.Equal(0x11000u - 16, _allocator.Allocate(16, MemoryFlags.Fast | MemoryFlags.Reverse));
    }

    [Fact]
    public void Allocate_Clear_ZeroesTheMemory()
    {
        _memory.Write32(0x10000, 0xFFFF_FFFF);

        var address = _allocator.Allocate(8, MemoryFlags.Clear);

        Assert.Equal(0u, _memory.Read32(address));
    }

    [Fact]
    public void Allocate_TooLarge_ReturnsZero()
    {
        Assert.Equal(0u, _allocator.Allocate(0x2000, MemoryFlags.Chip));
    }

    [Fact]
    public void Free_MergesNeighbours()
    {
        var a = _allocator.Allocate(0x800, MemoryFlags.Fast);
        var b = _allocator.Allocate(0x800, MemoryFlags.Fast);
        Assert.Equal(0u, _allocator.Available(MemoryFlags.Fast));

        _allocator.Free(a, 0x800);
        _allocator.Free(b, 0x800);

        Assert.Equal(0x1000u, _allocator.Available(MemoryFlags.Fast | MemoryFlags.Largest));
    }

    [Fact]
    public void Free_Twice_Throws()
    {
        var address = _allocator.Allocate(16, MemoryFlags.Fast);
        _allocator.Free(address, 16);

        Assert.Throws<InvalidOperationException>(() => _allocator.Free(address, 16));
    }

    [Fact]
    public void Reserve_RemovesTheRange()
    {
        _allocator.Reserve(0x10000, 0x100);

        Assert.Equal(0x10100u, _allocator.Allocate(8, MemoryFlags.Fast));
    }

    [Fact]
    public void Available_Total_IsTheSizeOfAllRegions()
    {
        _allocator.Allocate(0x100, MemoryFlags.Any);

        Assert.Equal(0x2000u, _allocator.Available(MemoryFlags.Total));
        Assert.Equal(0x1F00u, _allocator.Available(MemoryFlags.Any));
    }

    [Fact]
    public void TypeOf_ReportsChipOrFast()
    {
        Assert.True(_allocator.TypeOf(0x1800).HasFlag(MemoryFlags.Chip));
        Assert.True(_allocator.TypeOf(0x10800).HasFlag(MemoryFlags.Fast));
        Assert.Equal((MemoryFlags)0, _allocator.TypeOf(0x5000));
    }
}
