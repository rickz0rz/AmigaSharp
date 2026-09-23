using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Cpu;

namespace AmigaSharp.Tests;

public class CpuStateTests
{
    private readonly Memory _memory = new();
    private readonly CpuState _cpu;

    public CpuStateTests()
    {
        _cpu = new CpuState(_memory) { Sp = 0x8000 };
    }

    [Fact]
    public void SetDataByte_KeepsUpperBits()
    {
        _cpu.D[3] = 0x1234_5678;

        _cpu.SetDataByte(3, 0xFFFF_FFAB);

        Assert.Equal(0x1234_56ABu, _cpu.D[3]);
    }

    [Fact]
    public void SetDataWord_KeepsUpperBits()
    {
        _cpu.D[3] = 0x1234_5678;

        _cpu.SetDataWord(3, 0xABCD);

        Assert.Equal(0x1234_ABCDu, _cpu.D[3]);
    }

    [Theory]
    [InlineData(0x7FFFu, 0x0000_7FFFu)]
    [InlineData(0x8000u, 0xFFFF_8000u)]
    public void SetAddressWord_SignExtends(uint value, uint expected)
    {
        _cpu.SetAddressWord(2, value);

        Assert.Equal(expected, _cpu.A[2]);
    }

    [Fact]
    public void Push32_WritesToMemoryBelowStackPointer()
    {
        _cpu.Push32(0xDEAD_BEEF);

        Assert.Equal(0x7FFCu, _cpu.Sp);
        Assert.Equal(0xDEAD_BEEFu, _memory.Read32(0x7FFC));
        Assert.Equal(0xDEAD_BEEFu, _cpu.Pop32());
        Assert.Equal(0x8000u, _cpu.Sp);
    }

    [Fact]
    public void Push16_MovesStackPointerByTwo()
    {
        _cpu.Push16(0x1234);

        Assert.Equal(0x7FFEu, _cpu.Sp);
        Assert.Equal(0x1234, _cpu.Pop16());
    }

    [Fact]
    public void Ccr_PacksAndUnpacksFlags()
    {
        _cpu.Ccr = 0x15;

        Assert.True(_cpu.X);
        Assert.False(_cpu.N);
        Assert.True(_cpu.Z);
        Assert.False(_cpu.V);
        Assert.True(_cpu.C);
        Assert.Equal(0x15, _cpu.Ccr);
    }

    [Fact]
    public void SetLogicFlags_SetsNAndZ_ClearsVAndC_KeepsX()
    {
        _cpu.Ccr = 0x1F;

        _cpu.SetLogicFlags16(0x0001_8000);

        Assert.True(_cpu.X);
        Assert.True(_cpu.N);
        Assert.False(_cpu.Z);
        Assert.False(_cpu.V);
        Assert.False(_cpu.C);
    }

    [Fact]
    public void SetLogicFlags8_UsesOnlyLowByte()
    {
        _cpu.SetLogicFlags8(0x1234_5600);

        Assert.True(_cpu.Z);
        Assert.False(_cpu.N);
    }
}
