using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Libraries;

namespace AmigaSharp.Tests.Libraries;

public sealed class ExecLibraryTests : IDisposable
{
    private const short Supervisor = -30;
    private const short Forbid = -132;
    private const short Permit = -138;
    private const short AllocMem = -198;
    private const short FreeMem = -210;
    private const short AvailMem = -216;
    private const short FindTask = -294;
    private const short SetSignal = -306;
    private const short Wait = -318;
    private const short Signal = -324;
    private const short AllocSignal = -330;
    private const short FreeSignal = -336;
    private const short PutMsg = -366;
    private const short GetMsg = -372;
    private const short ReplyMsg = -378;
    private const short WaitPort = -384;
    private const short SetFunction = -420;
    private const short OpenDevice = -444;
    private const short RawDoFmt = -522;
    private const short CopyMem = -624;
    private const short CreateMsgPort = -666;
    private const short DeleteMsgPort = -672;
    private const short AllocVec = -684;
    private const short FreeVec = -690;

    private readonly LibraryHarness _harness = new();
    private uint Exec => _harness.ExecBase;
    private Memory Memory => _harness.Memory;

    [Fact]
    public void AllocMem_FreeMem_ChangeAvailMem()
    {
        var before = _harness.Call(Exec, AvailMem, ("D1", (uint)MemoryFlags.Chip));

        var block = _harness.Call(Exec, AllocMem, ("D0", 1000), ("D1", (uint)(MemoryFlags.Chip | MemoryFlags.Clear)));

        Assert.InRange(block, Core.ChipStart, Core.ChipEnd);
        Assert.Equal(before - 1000, _harness.Call(Exec, AvailMem, ("D1", (uint)MemoryFlags.Chip)));
        _harness.Call(Exec, FreeMem, ("A1", block), ("D0", 1000));
        Assert.Equal(before, _harness.Call(Exec, AvailMem, ("D1", (uint)MemoryFlags.Chip)));
    }

    [Fact]
    public void AllocVec_KeepsTheSize_ForFreeVec()
    {
        var before = _harness.Call(Exec, AvailMem, ("D1", 0));

        var block = _harness.Call(Exec, AllocVec, ("D0", 100), ("D1", (uint)MemoryFlags.Clear));
        _harness.Call(Exec, FreeVec, ("A1", block));

        Assert.NotEqual(0u, block);
        Assert.Equal(before, _harness.Call(Exec, AvailMem, ("D1", 0)));
    }

    [Fact]
    public void FindTask_Null_ReturnsTheMainProcess()
    {
        Assert.Equal(_harness.Core.MainProcess, _harness.Call(Exec, FindTask, ("A1", 0)));
    }

    [Fact]
    public void Signals_AllocateSetAndWait()
    {
        var signal = (int)_harness.Call(Exec, AllocSignal, ("D0", unchecked((uint)-1)));
        Assert.Equal(31, signal);
        var mask = 1u << signal;

        _harness.Call(Exec, Signal, ("A1", _harness.Core.MainProcess), ("D0", mask));

        Assert.Equal(mask, _harness.Call(Exec, SetSignal, ("D0", 0), ("D1", 0)) & mask);
        Assert.Equal(mask, _harness.Call(Exec, Wait, ("D0", mask | 0x1000)));
        Assert.Equal(0u, _harness.Call(Exec, SetSignal, ("D0", 0), ("D1", 0)) & mask);
        _harness.Call(Exec, FreeSignal, ("D0", (uint)signal));
        Assert.Equal(31u, _harness.Call(Exec, AllocSignal, ("D0", unchecked((uint)-1))));
    }

    [Fact]
    public void Wait_ForASignalThatCannotArrive_Throws()
    {
        Assert.Throws<WaitDeadlockException>(() => _harness.Call(Exec, Wait, ("D0", 0x8000_0000)));
    }

    [Fact]
    public void Messages_PutGetAndReply()
    {
        var port = _harness.Call(Exec, CreateMsgPort);
        var replyPort = _harness.Call(Exec, CreateMsgPort);
        var message = _harness.Call(Exec, AllocMem, ("D0", MessageOffsets.Size), ("D1", (uint)MemoryFlags.Clear));
        Memory.Write32(message + MessageOffsets.ReplyPort, replyPort);

        _harness.Call(Exec, PutMsg, ("A0", port), ("A1", message));

        Assert.Equal(message, _harness.Call(Exec, WaitPort, ("A0", port)));
        Assert.Equal(message, _harness.Call(Exec, GetMsg, ("A0", port)));
        Assert.Equal(0u, _harness.Call(Exec, GetMsg, ("A0", port)));

        _harness.Call(Exec, ReplyMsg, ("A1", message));

        Assert.Equal(NodeType.ReplyMessage, Memory.Read8(message + NodeOffsets.Type));
        Assert.Equal(message, _harness.Call(Exec, GetMsg, ("A0", replyPort)));
        _harness.Call(Exec, DeleteMsgPort, ("A0", port));
        _harness.Call(Exec, DeleteMsgPort, ("A0", replyPort));
    }

    [Fact]
    public void ForbidPermit_ChangeTheNestCount()
    {
        _harness.Call(Exec, Forbid);
        Assert.Equal(0, Memory.Read8(Exec + ExecBaseOffsets.TaskDisableCount));

        _harness.Call(Exec, Permit);
        Assert.Equal(0xFF, Memory.Read8(Exec + ExecBaseOffsets.TaskDisableCount));
    }

    [Fact]
    public void CopyMem_CopiesBytes()
    {
        var source = _harness.String("copy me");
        var destination = _harness.Core.AllocateSystem(16);

        _harness.Call(Exec, CopyMem, ("A0", source), ("A1", destination), ("D0", 8));

        Assert.Equal("copy me", Memory.ReadCString(destination));
    }

    [Fact]
    public void RawDoFmt_CallsA68000PutChProc()
    {
        // StuffChar: MOVE.B D0,(A3)+ / RTS. The interpreter runs it.
        var stuffChar = _harness.Core.AllocateSystem([0x16, 0xC0, 0x4E, 0x75]);
        var format = _harness.String("%s has %d items");
        var data = _harness.Core.AllocateSystem(8);
        Memory.Write32(data, _harness.String("list"));
        Memory.Write16(data + 4, 3);
        var buffer = _harness.Core.AllocateSystem(64);

        var next = _harness.Call(Exec, RawDoFmt, ("A0", format), ("A1", data), ("A2", stuffChar), ("A3", buffer));

        Assert.Equal("list has 3 items", Memory.ReadCString(buffer));
        Assert.Equal(data + 6, next);
    }

    [Fact]
    public void Supervisor_RunsTheFunctionInSupervisorMode_AndReturns()
    {
        // MOVE SR,D1 / RTE
        var function = _harness.Core.AllocateSystem([0x40, 0xC1, 0x4E, 0x73]);
        var stackPointer = _harness.Core.Cpu.Sp;

        _harness.Call(Exec, Supervisor, ("A5", function));

        Assert.NotEqual(0u, _harness.Core.Cpu.D[1] & 0x2000);
        Assert.False(_harness.Core.Cpu.S);
        Assert.Equal(stackPointer, _harness.Core.Cpu.Sp);
    }

    [Fact]
    public void SetFunction_ChangesTheVector_AndReturnsTheOldFunction()
    {
        // MOVEQ #42,D0 / RTS
        var replacement = _harness.Core.AllocateSystem([0x70, 0x2A, 0x4E, 0x75]);
        var oldFunction = _harness.Call(Exec, SetFunction, ("A1", Exec), ("A0", unchecked((uint)FindTask)), ("D0", replacement));

        Assert.InRange(oldFunction, LibraryManager.StubBase, LibraryManager.StubBase + 0x1000);
        Assert.Equal(42u, _harness.Call(Exec, FindTask, ("A1", 0)));
    }

    [Fact]
    public void OpenDevice_Unknown_FailsWithOpenFail()
    {
        var request = _harness.Core.AllocateSystem(IoRequestOffsets.StandardSize);

        var error = (int)_harness.Call(Exec, OpenDevice, ("A0", _harness.String("nosuch.device")), ("D0", 0), ("A1", request), ("D1", 0));

        Assert.Equal(IoRequestOffsets.ErrorOpenFail, error);
        Assert.Equal(IoRequestOffsets.ErrorOpenFail, (sbyte)Memory.Read8(request + IoRequestOffsets.Error));
    }

    [Fact]
    public void ExecBase_HasTheLibraryListAndTheVersion()
    {
        Assert.Equal(40, Memory.Read16(Exec + LibraryOffsets.Version));
        Assert.Equal(60, Memory.Read8(Exec + ExecBaseOffsets.VBlankFrequency));
        Assert.Contains(_harness.DosBase, ExecList.Nodes(Memory, Exec + ExecBaseOffsets.LibraryList));
    }

    public void Dispose() => _harness.Dispose();
}
