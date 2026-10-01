using AmigaSharp.Runtime;

namespace AmigaSharp.Tests;

public class MemoryTests
{
    private readonly Memory _memory = new();

    [Fact]
    public void Write32_StoresBigEndian()
    {
        _memory.Write32(0x1000, 0x1234_5678);

        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, _memory.ReadBytes(0x1000, 4));
        Assert.Equal(0x1234, _memory.Read16(0x1000));
        Assert.Equal(0x5678, _memory.Read16(0x1002));
        Assert.Equal(0x1234_5678u, _memory.Read32(0x1000));
    }

    [Fact]
    public void Address_IgnoresTopEightBits()
    {
        _memory.Write32(0xFF00_2000, 0xCAFE_BABE);

        Assert.Equal(0xCAFE_BABEu, _memory.Read32(0x2000));
    }

    [Theory]
    [InlineData(0x1001u)]
    [InlineData(0x1003u)]
    public void WordAndLongAccess_AtOddAddress_ThrowsAddressError(uint address)
    {
        Assert.Throws<AddressErrorException>(() => _memory.Read16(address));
        Assert.Throws<AddressErrorException>(() => _memory.Read32(address));
        Assert.Throws<AddressErrorException>(() => _memory.Write16(address, 0));
        Assert.Throws<AddressErrorException>(() => _memory.Write32(address, 0));
    }

    [Fact]
    public void WordAndLongAccess_AtOddAddress_WorksWhenAllowed_AsOnA68020()
    {
        _memory.AllowUnaligned = true;

        _memory.Write32(0x1001, 0x11223344);
        _memory.Write16(0x1005, 0x5566);

        Assert.Equal(0x1122u, _memory.Read16(0x1001));
        Assert.Equal(0x33445566u, _memory.Read32(0x1003));
        Assert.Equal(0x22, _memory.Read8(0x1002));
    }

    [Fact]
    public void ByteAccess_AtOddAddress_Works()
    {
        _memory.Write8(0x1001, 0xAB);

        Assert.Equal(0xAB, _memory.Read8(0x1001));
    }

    [Theory]
    [InlineData(0xDFF180u)] // COLOR00
    [InlineData(0xBFE001u)] // CIA-A PRA
    public void CustomChipAndCiaAccess_Throws(uint address)
    {
        Assert.Throws<HardwareAccessException>(() => _memory.Read8(address));
        Assert.Throws<HardwareAccessException>(() => _memory.Write16(address & ~1u, 0));
    }

    [Fact]
    public void ReadCString_StopsAtZeroByte()
    {
        // "café" in ISO-8859-1, a zero byte, then more data.
        _memory.WriteBytes(0x3000, [0x63, 0x61, 0x66, 0xE9, 0x00, 0x6A]);

        Assert.Equal("café", _memory.ReadCString(0x3000));
    }
}
