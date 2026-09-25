using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

public class SerialAndAudioTests
{
    private readonly ManualClock _clock = new();
    private readonly Memory _memory = new();
    private readonly Chipset _chipset;
    private readonly MemorySerialConnection _connection = new();

    public SerialAndAudioTests()
    {
        _chipset = new Chipset(_clock);
        _memory.Hardware = _chipset;
        _chipset.Custom.Serial.Connection = _connection;
        // 9600 baud.
        _memory.Write16(0xDFF032, (ushort)(Beam.ColorClockHz / 9600 - 1));
    }

    [Fact]
    public void ReceivedByte_GoesToSerdatr_AndRequestsRbf()
    {
        _connection.Send(0x41);

        _chipset.Custom.Update();

        Assert.Equal(1 << InterruptBit.Rbf, _chipset.Custom.Intreq & (1 << InterruptBit.Rbf));
        // RBF, TBE, TSRE, the idle receive line, the stop bit and the data.
        Assert.Equal(0x4000 | 0x3800 | 0x0100 | 0x41, _memory.Read16(0xDFF018));
    }

    [Fact]
    public void NextByte_WaitsWhileRbfIsPending()
    {
        _connection.Send(1, 2);
        _chipset.Custom.Update();

        _clock.Advance(TimeSpan.FromMilliseconds(5));
        _chipset.Custom.Update();

        Assert.Equal(1, _memory.Read16(0xDFF018) & 0xFF);
    }

    [Fact]
    public void NextByte_WaitsForTheByteTime_AfterRbfClears()
    {
        _connection.Send(1, 2);
        _chipset.Custom.Update();

        _memory.Write16(0xDFF09C, 1 << InterruptBit.Rbf);
        _chipset.Custom.Update();
        Assert.Equal(1, _memory.Read16(0xDFF018) & 0xFF);

        // One byte at 9600 baud takes 10 bits: about 1.04 ms.
        _clock.Advance(TimeSpan.FromMilliseconds(1.1));
        _chipset.Custom.Update();
        Assert.Equal(2, _memory.Read16(0xDFF018) & 0xFF);
        Assert.Equal(1 << InterruptBit.Rbf, _chipset.Custom.Intreq & (1 << InterruptBit.Rbf));
    }

    [Fact]
    public void Serdat_SendsTheByte_AndRequestsTbe()
    {
        _memory.Write16(0xDFF030, 0x015A);

        Assert.Equal(new byte[] { 0x5A }, _connection.Sent.ToArray());
        Assert.Equal(1, _chipset.Custom.Intreq & 1);
    }

    [Fact]
    public void VerticalBlank_IsRequestedEachFrame()
    {
        _clock.Advance(TimeSpan.FromMilliseconds(20));
        _chipset.Custom.Update();

        Assert.Equal(1 << InterruptBit.VerticalBlank, _chipset.Custom.Intreq & (1 << InterruptBit.VerticalBlank));
    }

    [Fact]
    public void AudioDma_RequestsItsInterruptAtTheRateOfTheBuffer()
    {
        // Prevue uses channel 1 as a timer: a buffer of 1 word and a period of $65B color clocks.
        _memory.Write16(0xDFF0B4, 1);
        _memory.Write16(0xDFF0B6, 0x65B);
        _memory.Write16(0xDFF096, 0x8202);
        var interval = TimeSpan.FromSeconds(2 * 0x65B / Beam.ColorClockHz);

        _chipset.Custom.Update();
        Assert.NotEqual(0, _chipset.Custom.Intreq & 0x100);

        _memory.Write16(0xDFF09C, 0x100);
        _clock.Advance(interval * 0.5);
        _chipset.Custom.Update();
        Assert.Equal(0, _chipset.Custom.Intreq & 0x100);

        _clock.Advance(interval * 0.6);
        _chipset.Custom.Update();
        Assert.NotEqual(0, _chipset.Custom.Intreq & 0x100);
    }

    [Fact]
    public void AudioDma_Off_StopsTheInterrupts()
    {
        _memory.Write16(0xDFF0B4, 1);
        _memory.Write16(0xDFF0B6, 200);
        _memory.Write16(0xDFF096, 0x8202);
        _chipset.Custom.Update();
        _memory.Write16(0xDFF096, 0x0002);
        _memory.Write16(0xDFF09C, 0x100);

        _clock.Advance(TimeSpan.FromMilliseconds(10));
        _chipset.Custom.Update();

        Assert.Equal(0, _chipset.Custom.Intreq & 0x100);
    }
}
