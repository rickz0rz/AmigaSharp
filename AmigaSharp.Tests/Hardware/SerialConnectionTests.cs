using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

public class SerialConnectionTests
{
    private readonly ManualClock _clock = new();
    private readonly Memory _memory = new();
    private readonly Chipset _chipset;

    public SerialConnectionTests()
    {
        _chipset = new Chipset(_clock, _memory);
        _memory.Hardware = _chipset;
        // 9600 baud: one byte takes about 1.04 ms.
        _memory.Write16(0xDFF032, (ushort)(VideoStandard.Ntsc.ColorClockHz / 9600 - 1));
    }

    [Fact]
    public void Replay_WaitsForItsStartCondition()
    {
        var start = false;
        var replay = new ReplaySerialConnection([0x41], () => start);
        _chipset.Custom.Serial.Connection = replay;

        _chipset.Custom.Update();
        Assert.Equal(0, _chipset.Custom.Intreq & (1 << InterruptBit.Rbf));
        Assert.Equal(0, replay.Available);

        start = true;
        _chipset.Custom.Update();
        Assert.Equal(0x41, _memory.Read16(0xDFF018) & 0xFF);
        Assert.True(replay.Finished);
    }

    [Fact]
    public void Replay_GivesEachByteAtTheBaudRate()
    {
        var replay = new ReplaySerialConnection([1, 2, 3]);
        _chipset.Custom.Serial.Connection = replay;
        var received = new List<int>();

        for (var step = 0; step < 40; step++)
        {
            _chipset.Custom.Update();
            if ((_chipset.Custom.Intreq & (1 << InterruptBit.Rbf)) != 0)
            {
                received.Add(_memory.Read16(0xDFF018) & 0xFF);
                _memory.Write16(0xDFF09C, 1 << InterruptBit.Rbf);
            }

            _clock.Advance(TimeSpan.FromMilliseconds(0.1));
        }

        // 4 ms is enough for 3 bytes. The replay does not lose a byte, and it does not go faster than the baud rate.
        Assert.Equal([1, 2, 3], received);
        Assert.Equal(3, replay.Position);
    }

    [Fact]
    public void Log_WritesTheTimeTheDirectionTheOffsetAndTheBytes()
    {
        var inner = new MemorySerialConnection();
        var log = new StringWriter();
        var time = TimeSpan.FromSeconds(1.5);
        var connection = new LoggingSerialConnection(inner, log, () => time);
        inner.Send(0x55, 0xAA, 0x41);

        connection.TryRead(out _);
        connection.TryRead(out _);
        connection.TryRead(out _);
        time += TimeSpan.FromMilliseconds(1);
        connection.Write(0x4D);
        connection.Dispose();

        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("#", lines[0]);
        Assert.Matches(@"^1\.500000\s+RX\s+00000000\s+55 AA 41\s+U\.A$", lines[1]);
        Assert.Matches(@"^1\.501000\s+TX\s+00000000\s+4D\s+M$", lines[2]);
        Assert.Equal(new byte[] { 0x4D }, inner.Sent.ToArray());
    }

    [Fact]
    public void Log_StartsANewLineAfterAGap()
    {
        var inner = new MemorySerialConnection();
        var log = new StringWriter();
        var time = TimeSpan.Zero;
        var connection = new LoggingSerialConnection(inner, log, () => time);
        inner.Send(1, 2);

        connection.TryRead(out _);
        time += TimeSpan.FromMilliseconds(50);
        connection.TryRead(out _);
        connection.Dispose();

        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Matches(@"RX\s+00000000\s+01\s", lines[1]);
        Assert.Matches(@"^0\.050000\s+RX\s+00000001\s+02\s", lines[2]);
    }
}
