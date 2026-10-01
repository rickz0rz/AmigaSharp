using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

public class ChipsetTests
{
    private readonly ManualClock _clock = new();
    private readonly Memory _memory = new();
    private readonly Chipset _chipset;

    public ChipsetTests()
    {
        _chipset = new Chipset(_clock, _memory);
        _memory.Hardware = _chipset;
    }

    [Fact]
    public void Vposr_HasTheEcsNtscAgnusId()
    {
        Assert.Equal(0x3000, _memory.Read16(0xDFF004) & 0x7F00);
    }

    [Fact]
    public void Dmacon_HasTheMasterBitOfKickstart_AndNoChannels()
    {
        Assert.Equal(0x0200, _memory.Read16(0xDFF002) & 0x03FF);
    }

    [Fact]
    public void BeamPosition_FollowsTheClock()
    {
        // 100 lines and 50 color clocks. Half a color clock more keeps the time inside that color clock.
        _clock.Elapsed = TimeSpan.FromSeconds((100 * Beam.ColorClocksPerLine + 50.5) / Beam.ColorClockHz);

        Assert.Equal((100 << 8) | 50, _memory.Read16(0xDFF006));
        Assert.Equal(100, _chipset.Beam.Line);

        _clock.Elapsed = TimeSpan.FromSeconds(1.0);
        // 3579545 / (227 * 262) = 60.2 frames each second.
        Assert.Equal(60, _chipset.Beam.Frame);
    }

    [Fact]
    public void Line256AndMore_SetBit0OfVposr()
    {
        _clock.Elapsed = TimeSpan.FromSeconds((260 * Beam.ColorClocksPerLine + 0.5) / Beam.ColorClockHz);

        Assert.Equal(1, _memory.Read16(0xDFF004) & 1);
        Assert.Equal(4, _memory.Read16(0xDFF006) >> 8);
    }

    [Fact]
    public void Dmacon_UsesTheSetClearBit()
    {
        _memory.Write16(0xDFF096, 0x8000 | 0x0180);
        _memory.Write16(0xDFF096, 0x0080);

        // DMAEN (bit 9) is on from the start.
        Assert.Equal(0x0300, _memory.Read16(0xDFF002));
    }

    [Fact]
    public void Intena_UsesTheSetClearBit()
    {
        _memory.Write16(0xDFF09A, 0xC020);
        _memory.Write16(0xDFF09A, 0x4000);

        Assert.Equal(0x0020, _memory.Read16(0xDFF01C));
    }

    [Fact]
    public void ByteWrite_PutsTheByteInBothHalves()
    {
        _memory.Write8(0xDFF181, 0x0F);

        Assert.Equal(0x0F0F, _chipset.Custom[CustomRegister.Color00]);
    }

    [Fact]
    public void RegisterWrites_AreStored()
    {
        _memory.Write32(0xDFF080, 0x0012_3456);

        Assert.Equal(0x0012, _chipset.Custom[CustomRegister.Cop1lc]);
        Assert.Equal(0x3456, _chipset.Custom[CustomRegister.Cop1lc + 2]);
    }

    [Fact]
    public void SerialTransmit_ReportsTheWord()
    {
        var sent = new List<ushort>();
        _chipset.Custom.SerialTransmit += sent.Add;

        _memory.Write16(0xDFF030, 0x0141);

        Assert.Equal([0x0141], sent);
    }

    [Fact]
    public void ReadOfAnUnknownRegister_Throws()
    {
        Assert.Throws<HardwareAccessException>(() => _memory.Read16(0xDFF0A0));
    }

    [Theory]
    [InlineData(0xC00000u)] // Slow memory.
    [InlineData(0xDE0000u)] // Gayle and Ramsey of the later models.
    [InlineData(0xE80000u)] // The configuration space of the expansion boards.
    public void AccessToAnAddressThatAnA2000DoesNotHave_IsAnOpenBus(uint address)
    {
        _memory.Write8(address, 0x55);
        _memory.Write16(address & ~1u, 0x5555);

        Assert.Equal(0, _memory.Read8(address));
        Assert.Equal(0, _memory.Read16(address & ~1u));
    }

    [Fact]
    public void Cia_PortMixesOutputsAndInputs()
    {
        _chipset.CiaA.InputA = 0b1010_1010;
        _memory.Write8(0xBFE201, 0b1111_0000); // CIA-A DDRA: the high bits are outputs.
        _memory.Write8(0xBFE001, 0b0101_0101); // CIA-A PRA

        Assert.Equal(0b0101_1010, _memory.Read8(0xBFE001));
    }

    [Fact]
    public void Cia_BUsesTheEvenAddresses()
    {
        _chipset.CiaB.InputA = 0b1110_1111;

        Assert.Equal(0b1110_1111, _memory.Read8(0xBFD000));
        _memory.Write8(0xBFD100, 0x42);
        Assert.Equal(0x42, _chipset.CiaB.OutputB);
    }

    [Fact]
    public void Cia_TimeOfDayCountsFrames()
    {
        _clock.Elapsed = TimeSpan.FromSeconds(2);

        Assert.Equal(120, _memory.Read8(0xBFE801));
    }
}
