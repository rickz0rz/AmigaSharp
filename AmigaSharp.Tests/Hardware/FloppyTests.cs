using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

public class FloppyTests
{
    private const uint CiaAPra = 0xBFE001;
    private const uint CiaBPrb = 0xBFD100;
    private const uint CiaBIcr = 0xBFDD00;
    private const uint Buffer = 0x1_0000;
    private const byte Ready = 0x20, TrackZero = 0x10, Change = 0x04;

    // PRB: motor on (bit 7 low) and DF0 selected (bit 3 low), side 0, direction out, no step.
    private const byte SelectDf0WithMotor = 0x77;

    private readonly ManualClock _clock = new();
    private readonly Memory _memory = new();
    private readonly Chipset _chipset;
    private readonly byte[] _image = new byte[901_120];

    public FloppyTests()
    {
        _chipset = new Chipset(_clock, _memory);
        _memory.Hardware = _chipset;
        // Set the lines high before they become outputs, as Kickstart does. Else a step pulse and a motor start happen.
        _memory.Write8(CiaBPrb, 0xFF);
        _memory.Write8(0xBFD300, 0xFF); // DDRB of CIA-B: the drive lines are outputs.
        new Random(1).NextBytes(_image);
        _chipset.Disks.Drives[0].Insert(_image);
    }

    [Fact]
    public void EncodedTrack_HasElevenValidSectorsWithTheDataOfTheImage()
    {
        const int track = 37;
        var words = AmigaDosMfm.EncodeTrack(_image, track);

        var sectors = new HashSet<int>();
        for (var i = 0; i < words.Length - 1; i++)
        {
            if (words[i] != 0x4489 || words[i + 1] != 0x4489)
                continue;
            var start = i + 2;
            var info = Decode(words, start, 0, 1)[0];
            Assert.Equal(0xFFu, info >> 24);
            Assert.Equal((uint)track, (info >> 16) & 0xFF);
            var sector = (int)(info >> 8) & 0xFF;
            Assert.Equal((uint)(11 - sector), info & 0xFF);

            Assert.Equal(DataBitsXor(words, start, 10), Decode(words, start, 10, 1)[0]);
            Assert.Equal(DataBitsXor(words, start + 28, 256), Decode(words, start, 12, 1)[0]);
            var data = Decode(words, start, 14, 128);
            var expected = _image.AsSpan((track * 11 + sector) * 512, 512);
            for (var j = 0; j < 128; j++)
                Assert.Equal((uint)(expected[j * 4] << 24 | expected[j * 4 + 1] << 16 | expected[j * 4 + 2] << 8 | expected[j * 4 + 3]), data[j]);
            sectors.Add(sector);
            i = start;
        }

        Assert.Equal(Enumerable.Range(0, 11), sectors.Order());
    }

    [Fact]
    public void EncodedTrack_FollowsTheMfmRule_OutsideTheSyncWords()
    {
        var words = AmigaDosMfm.EncodeTrack(_image, 0);
        var previous = 0;
        foreach (var word in words)
        {
            for (var bit = 15; bit >= 0; bit--)
            {
                var value = (word >> bit) & 1;
                if (word != 0x4489)
                    Assert.False(value == 1 && previous == 1, "Two 1 bits are next to each other.");
                previous = value;
            }
        }
    }

    [Fact]
    public void Drive_IsReadyAfterTheMotorStarts_AndReportsTrackZeroAndTheChange()
    {
        _memory.Write8(CiaBPrb, SelectDf0WithMotor);
        _chipset.Disks.Update();
        Assert.Equal(Ready, _memory.Read8(CiaAPra) & Ready);
        Assert.Equal(0, _memory.Read8(CiaAPra) & (TrackZero | Change));

        _clock.Elapsed = TimeSpan.FromSeconds(0.5);
        _chipset.Disks.Update();
        Assert.Equal(0, _memory.Read8(CiaAPra) & Ready);

        // A step inward (DIR low): the heads leave cylinder 0, and the change flag clears.
        _memory.Write8(CiaBPrb, SelectDf0WithMotor & ~0x02);
        _memory.Write8(CiaBPrb, SelectDf0WithMotor & ~0x03);
        _memory.Write8(CiaBPrb, SelectDf0WithMotor & ~0x02);
        Assert.Equal(1, _chipset.Disks.Drives[0].Cylinder);
        Assert.Equal(TrackZero | Change, _memory.Read8(CiaAPra) & (TrackZero | Change));
    }

    [Fact]
    public void DmaRead_WaitsForTheSyncWord_AndCopiesTheWordsAfterIt()
    {
        StartMotor();

        Read(words: 100);

        Assert.NotEqual(0, _chipset.Custom.Intreq & (1 << InterruptBit.DiskBlock));
        // A sector starts with the second sync word, then the info long of the sector.
        Assert.Equal(0x4489, _memory.Read16(Buffer));
        var info = Decode(Enumerable.Range(0, 100).Select(i => _memory.Read16(Buffer + (uint)i * 2)).ToArray(), 1, 0, 1)[0];
        Assert.Equal(0xFF00_0000u, info & 0xFFFF_0000);
    }

    [Fact]
    public void DmaWrite_ChangesTheTrack_AndARead_GetsTheWrittenWords()
    {
        StartMotor();
        for (var i = 0; i < 200; i++)
            _memory.Write16(Buffer + (uint)i * 2, i == 50 ? (ushort)0x4489 : (ushort)(0x1000 + i));
        _memory.Write16(0xDFF09E, 0x7F00); // ADKCON: no word sync.
        Start(0xC000 | 200);

        _memory.Write16(0xDFF09E, 0x9500); // ADKCON: MFM and word sync.
        Read(words: 10);

        Assert.Equal(0x1000 + 51, _memory.Read16(Buffer));
    }

    [Fact]
    public void DmaRead_WithoutADisk_NeverEnds()
    {
        _chipset.Disks.Drives[0].Eject();
        StartMotor();

        Read(words: 100);

        Assert.Equal(0, _chipset.Custom.Intreq & (1 << InterruptBit.DiskBlock));
    }

    [Fact]
    public void TurningDisk_SendsAnIndexPulseToTheFlagOfCiaB()
    {
        StartMotor();
        _memory.Read8(CiaBIcr);

        _clock.Elapsed += TimeSpan.FromSeconds(0.2);
        _chipset.Disks.Update();

        Assert.Equal(CiaInterrupt.Flag, _memory.Read8(CiaBIcr) & CiaInterrupt.Flag);
    }

    private void StartMotor()
    {
        _memory.Write8(CiaBPrb, SelectDf0WithMotor);
        _clock.Elapsed = TimeSpan.FromSeconds(0.5);
        _chipset.Disks.Update();
        _memory.Write16(0xDFF096, 0x8210); // DMACON: DMA and disk DMA.
    }

    private void Read(int words)
    {
        _memory.Write16(0xDFF09E, 0x9500); // ADKCON: MFM and word sync.
        _memory.Write16(0xDFF07E, 0x4489); // DSKSYNC
        Start(0x8000 | words);
    }

    private void Start(int length)
    {
        _memory.Write32(0xDFF020, Buffer);
        _memory.Write16(0xDFF024, (ushort)length);
        _memory.Write16(0xDFF024, (ushort)length);
    }

    /// <summary>Decodes longs of a block: the odd bits are at <paramref name="offset"/>, and the even bits after them.</summary>
    private static uint[] Decode(ushort[] words, int start, int offset, int count)
    {
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            var odd = Long(words, start + (offset + i) * 2);
            var even = Long(words, start + (offset + count + i) * 2);
            result[i] = (odd & 0x5555_5555) << 1 | (even & 0x5555_5555);
        }

        return result;
    }

    private static uint DataBitsXor(ushort[] words, int start, int longs)
    {
        var sum = 0u;
        for (var i = 0; i < longs; i++)
            sum ^= Long(words, start + i * 2);
        return sum & 0x5555_5555;
    }

    private static uint Long(ushort[] words, int index) => (uint)(words[index] << 16 | words[index + 1]);
}
