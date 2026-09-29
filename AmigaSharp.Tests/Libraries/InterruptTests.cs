using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Libraries;

/// <summary>Tests of the delivery of interrupts to handlers and servers in 68000 code.</summary>
public class InterruptTests
{
    private const short Disable = -120;
    private const short Enable = -126;
    private const short SetIntVector = -162;
    private const short AddIntServer = -168;
    private const short Wait = -318;
    private const short AllocSignal = -330;

    private readonly ManualClock _clock = new();
    private readonly Core _core;

    public InterruptTests()
    {
        _core = new Core(new MemoryStream(), new MemoryStream(), Path.GetTempPath(), _clock) { Log = TextWriter.Null };
    }

    private Memory Memory => _core.Memory;

    [Fact]
    public void Handler_GetsTheInterrupt_AndTheRegistersDoNotChange()
    {
        var counter = InstallCountingHandler(InterruptBit.Rbf, clearsRequest: true);
        EnableInterrupt(InterruptBit.Rbf);
        _core.Cpu.D[0] = 0x1234_5678;
        _core.Cpu.A[1] = 0xCAFE;
        _core.Cpu.Ccr = 0x15;

        _core.Chipset.Custom.RequestInterrupt(InterruptBit.Rbf);
        _core.PollNow();

        Assert.Equal(1u, Memory.Read32(counter));
        Assert.Equal((0x1234_5678u, 0xCAFEu, (byte)0x15), (_core.Cpu.D[0], _core.Cpu.A[1], _core.Cpu.Ccr));
        Assert.False(_core.Cpu.S);
    }

    [Fact]
    public void AutovectorOfTheProgram_GetsTheInterrupt_AsOnA68000()
    {
        var counter = _core.AllocateSystem(4);
        // ADDQ.L #1,counter; MOVE SR,flag; MOVE.W #$0020,$DFF09C; RTE.
        var flag = _core.AllocateSystem(2);
        byte[] code =
        [
            0x52, 0xB9, (byte)(counter >> 24), (byte)(counter >> 16), (byte)(counter >> 8), (byte)counter,
            0x40, 0xF9, (byte)(flag >> 24), (byte)(flag >> 16), (byte)(flag >> 8), (byte)flag,
            0x33, 0xFC, 0x00, 0x20, 0x00, 0xDF, 0xF0, 0x9C,
            0x4E, 0x73,
        ];
        Memory.Write32(0x6C, _core.AllocateSystem(code));
        EnableInterrupt(InterruptBit.VerticalBlank);
        _core.Cpu.D[0] = 0x1234_5678;

        _core.Chipset.Custom.RequestInterrupt(InterruptBit.VerticalBlank);
        _core.PollNow();

        Assert.Equal(1u, Memory.Read32(counter));
        // The handler runs in supervisor mode with the interrupt mask at level 3.
        Assert.Equal(0x2300, Memory.Read16(flag) & 0x2700);
        Assert.Equal(0, _core.Chipset.Custom.Intreq & (1 << InterruptBit.VerticalBlank));
        Assert.Equal(0x1234_5678u, _core.Cpu.D[0]);
        Assert.False(_core.Cpu.S);
    }

    [Fact]
    public void HandlerThatDoesNotClearTheRequest_RunsOnceInAPoll()
    {
        var counter = InstallCountingHandler(InterruptBit.Rbf, clearsRequest: false);
        EnableInterrupt(InterruptBit.Rbf);

        _core.Chipset.Custom.RequestInterrupt(InterruptBit.Rbf);
        _core.PollNow();

        Assert.Equal(1u, Memory.Read32(counter));
    }

    [Fact]
    public void Disable_BlocksTheInterrupts_UntilEnable()
    {
        var counter = InstallCountingHandler(InterruptBit.Rbf, clearsRequest: true);
        EnableInterrupt(InterruptBit.Rbf);
        CallExec(Disable);

        _core.Chipset.Custom.RequestInterrupt(InterruptBit.Rbf);
        _core.PollNow();
        Assert.Equal(0u, Memory.Read32(counter));

        CallExec(Enable);
        _core.PollNow();
        Assert.Equal(1u, Memory.Read32(counter));
    }

    [Fact]
    public void CpuInterruptMask_BlocksTheLowerLevels()
    {
        var counter = InstallCountingHandler(InterruptBit.Rbf, clearsRequest: true);
        EnableInterrupt(InterruptBit.Rbf);
        _core.Cpu.InterruptMask = 5;

        _core.Chipset.Custom.RequestInterrupt(InterruptBit.Rbf);
        _core.PollNow();

        Assert.Equal(0u, Memory.Read32(counter));
    }

    [Fact]
    public void VerticalBlankServer_RunsEachFrame()
    {
        var counter = _core.AllocateSystem(4);
        // ADDQ.L #1,(A1) / MOVEQ #0,D0 / RTS: a server that lets the next servers run.
        var server = NewInterrupt(_core.AllocateSystem([0x52, 0x91, 0x70, 0x00, 0x4E, 0x75]), counter);
        _core.Cpu.D[0] = InterruptBit.VerticalBlank;
        _core.Cpu.A[1] = server;
        _core.CallVector(_core.ExecBase, AddIntServer);

        for (var frame = 0; frame < 3; frame++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(17));
            _core.PollNow();
        }

        Assert.Equal(3u, Memory.Read32(counter));
        Assert.Equal(0, _core.Chipset.Custom.Intreq & (1 << InterruptBit.VerticalBlank));
    }

    [Fact]
    public void Wait_EndsWhenAnInterruptSignalsTheTask()
    {
        _core.Cpu.D[0] = unchecked((uint)-1);
        _core.CallVector(_core.ExecBase, AllocSignal);
        var mask = 1u << (int)_core.Cpu.D[0];
        // A vertical blank server that signals the task: MOVE.L 4(A1),D0 / MOVEA.L (A1),A1 / JSR -324(A6) /
        // MOVEQ #0,D0 / RTS.
        var data = _core.AllocateSystem(8);
        Memory.Write32(data, _core.MainProcess);
        Memory.Write32(data + 4, mask);
        var code = _core.AllocateSystem([0x20, 0x29, 0x00, 0x04, 0x22, 0x51, 0x4E, 0xAE, 0xFE, 0xBC, 0x70, 0x00, 0x4E, 0x75]);
        _core.Cpu.D[0] = InterruptBit.VerticalBlank;
        _core.Cpu.A[1] = NewInterrupt(code, data);
        _core.CallVector(_core.ExecBase, AddIntServer);

        _core.Cpu.D[0] = mask;
        _core.CallVector(_core.ExecBase, Wait);

        Assert.Equal(mask, _core.Cpu.D[0]);
        Assert.InRange(_clock.Elapsed, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public void Wait_WithNothingThatCanInterrupt_Throws()
    {
        _core.Chipset.Custom.Write(CustomRegister.Intena, 0x3FFF);

        _core.Cpu.D[0] = 0x8000_0000;
        Assert.Throws<WaitDeadlockException>(() => _core.CallVector(_core.ExecBase, Wait));
    }

    /// <summary>Installs a handler that adds 1 to a counter. Returns the address of the counter.</summary>
    private uint InstallCountingHandler(int bit, bool clearsRequest)
    {
        var counter = _core.AllocateSystem(4);
        // ADDQ.L #1,(A1), then MOVE.W #mask,$9C(A0) to clear the request, then RTS.
        byte[] code = clearsRequest
            ? [0x52, 0x91, 0x31, 0x7C, (byte)(1 << bit >> 8), (byte)(1 << bit), 0x00, 0x9C, 0x4E, 0x75]
            : [0x52, 0x91, 0x4E, 0x75];
        _core.Cpu.D[0] = (uint)bit;
        _core.Cpu.A[1] = NewInterrupt(_core.AllocateSystem(code), counter);
        _core.CallVector(_core.ExecBase, SetIntVector);
        return counter;
    }

    private uint NewInterrupt(uint code, uint data)
    {
        var interrupt = _core.AllocateSystem(InterruptOffsets.Size);
        Memory.Write8(interrupt + NodeOffsets.Type, NodeType.Interrupt);
        Memory.Write32(interrupt + InterruptOffsets.Data, data);
        Memory.Write32(interrupt + InterruptOffsets.Code, code);
        return interrupt;
    }

    private void EnableInterrupt(int bit) => _core.Chipset.Custom.Write(CustomRegister.Intena, (ushort)(0x8000 | 1 << bit));

    private void CallExec(short offset) => _core.CallVector(_core.ExecBase, offset);
}
