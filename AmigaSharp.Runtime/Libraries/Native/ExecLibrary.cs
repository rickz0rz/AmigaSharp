using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// exec.library. The runtime has one task, the main process. So a task switch never occurs: Forbid and Disable only
/// count, and Wait returns when the signals are already present.
/// </summary>
public class ExecLibrary(Core core) : AbstractLibrary
{
    private const int InterruptCount = 16;
    private const uint InterruptVectorSize = 12;

    // The interrupts that have a chain of servers (AddIntServer). The others have one handler (SetIntVector).
    private static readonly int[] ServerChains = [3, 4, 5, 13, 15];

    private readonly Memory _memory = core.Memory;

    public override string Name => "exec.library";
    public override ushort Version => 40;
    public override ushort Revision => 10;
    public override string IdString => "exec 40.10 (15.7.93)";
    public override ushort PositiveSize => (ushort)ExecBaseOffsets.Size;
    public override short LowestOffset => -816;

    private uint ThisTask => _memory.Read32(Base + ExecBaseOffsets.ThisTask);

    public override void Initialize()
    {
        _memory.Write16(Base + ExecBaseOffsets.SoftVersion, Revision);
        _memory.Write32(Base + ExecBaseOffsets.MaxLocMem, Core.ChipEnd);
        _memory.Write32(Base + ExecBaseOffsets.MaxExtMem, Core.FastEnd);
        _memory.Write16(Base + ExecBaseOffsets.Quantum, 4);
        // The nest counts are -1 when task switches and interrupts are enabled.
        _memory.Write8(Base + ExecBaseOffsets.InterruptDisableCount, 0xFF);
        _memory.Write8(Base + ExecBaseOffsets.TaskDisableCount, 0xFF);
        // AttnFlags 0: a 68000 with no FPU.
        _memory.Write16(Base + ExecBaseOffsets.AttnFlags, 0);
        _memory.Write16(Base + ExecBaseOffsets.TaskTrapsAllocated, 0x8000);
        // Prevue ran on NTSC Amigas.
        _memory.Write8(Base + ExecBaseOffsets.VBlankFrequency, 60);
        _memory.Write8(Base + ExecBaseOffsets.PowerSupplyFrequency, 60);
        _memory.Write32(Base + ExecBaseOffsets.EClockFrequency, 715_909);

        foreach (var list in new[]
                 {
                     (ExecBaseOffsets.MemList, NodeType.Memory), (ExecBaseOffsets.ResourceList, NodeType.Resource),
                     (ExecBaseOffsets.DeviceList, NodeType.Device), (ExecBaseOffsets.InterruptList, NodeType.Interrupt),
                     (ExecBaseOffsets.LibraryList, NodeType.Library), (ExecBaseOffsets.PortList, NodeType.MsgPort),
                     (ExecBaseOffsets.TaskReady, NodeType.Task), (ExecBaseOffsets.TaskWait, NodeType.Task),
                     (ExecBaseOffsets.SemaphoreList, (byte)15),
                 })
            ExecList.Initialize(_memory, Base + list.Item1, list.Item2);

        foreach (var number in ServerChains)
        {
            var chain = core.AllocateSystem(ListOffsets.Size);
            ExecList.Initialize(_memory, chain, NodeType.Interrupt);
            _memory.Write32(InterruptVector(number), chain);
        }
    }

    // Supervisor(userFunction)
    //                A5
    // The function runs in supervisor mode and ends with RTE.
    [LibraryFunctionOffset(-30)]
    public void Supervisor([A5] uint function)
    {
        core.CallFromNative(function, supervisorFrame: true);
    }

    // Disable() and Enable(). Disable stops all interrupts with the master bit of INTENA. Enable starts them again when
    // the nest count is -1 again.
    [LibraryFunctionOffset(-120)]
    public void Disable()
    {
        core.Chipset.Custom.Write(CustomRegister.Intena, 1 << InterruptBit.Enable);
        ChangeNestCount(ExecBaseOffsets.InterruptDisableCount, 1);
    }

    [LibraryFunctionOffset(-126)]
    public void Enable()
    {
        ChangeNestCount(ExecBaseOffsets.InterruptDisableCount, -1);
        if ((sbyte)_memory.Read8(Base + ExecBaseOffsets.InterruptDisableCount) < 0)
            core.Chipset.Custom.Write(CustomRegister.Intena, 0x8000 | 1 << InterruptBit.Enable);
    }

    [LibraryFunctionOffset(-132)]
    public void Forbid() => ChangeNestCount(ExecBaseOffsets.TaskDisableCount, 1);

    [LibraryFunctionOffset(-138)]
    public void Permit() => ChangeNestCount(ExecBaseOffsets.TaskDisableCount, -1);

    // oldInterrupt = SetIntVector(intNumber, interrupt)
    // D0                          D0         A1
    [LibraryFunctionOffset(-162)]
    public uint SetIntVector([D0] int number, [A1] uint interrupt)
    {
        var vector = InterruptVector(number);
        var old = _memory.Read32(vector + 8);
        _memory.Write32(vector + 8, interrupt);
        _memory.Write32(vector + 0, interrupt == 0 ? 0 : _memory.Read32(interrupt + InterruptOffsets.Data));
        _memory.Write32(vector + 4, interrupt == 0 ? 0 : _memory.Read32(interrupt + InterruptOffsets.Code));
        return old;
    }

    // AddIntServer(intNum, interrupt)
    //              D0      A1
    [LibraryFunctionOffset(-168)]
    public void AddIntServer([D0] int number, [A1] uint interrupt)
    {
        ExecList.Enqueue(_memory, ServerChain(number), interrupt);
    }

    // RemIntServer(intNum, interrupt)
    //              D0      A1
    [LibraryFunctionOffset(-174)]
    public void RemIntServer([D0] int number, [A1] uint interrupt)
    {
        ExecList.Remove(_memory, interrupt);
    }

    // memoryBlock = AllocMem(byteSize, attributes)
    // D0                     D0        D1
    [LibraryFunctionOffset(-198)]
    public uint AllocMem([D0] uint size, [D1] uint attributes) =>
        core.Allocator.Allocate(size, (MemoryFlags)attributes);

    // memoryBlock = AllocAbs(byteSize, location)
    // D0                     D0        A1
    [LibraryFunctionOffset(-204)]
    public uint AllocAbs([D0] uint size, [A1] uint location)
    {
        core.Allocator.Reserve(location, size);
        return location;
    }

    // FreeMem(memoryBlock, byteSize)
    //         A1           D0
    [LibraryFunctionOffset(-210)]
    public void FreeMem([A1] uint block, [D0] uint size) => core.Allocator.Free(block, size);

    // size = AvailMem(attributes)
    // D0              D1
    [LibraryFunctionOffset(-216)]
    public uint AvailMem([D1] uint attributes) => core.Allocator.Available((MemoryFlags)attributes);

    [LibraryFunctionOffset(-234)]
    public void Insert([A0] uint list, [A1] uint node, [A2] uint predecessor) =>
        ExecList.Insert(_memory, list, node, predecessor);

    [LibraryFunctionOffset(-240)]
    public void AddHead([A0] uint list, [A1] uint node) => ExecList.AddHead(_memory, list, node);

    [LibraryFunctionOffset(-246)]
    public void AddTail([A0] uint list, [A1] uint node) => ExecList.AddTail(_memory, list, node);

    [LibraryFunctionOffset(-252)]
    public void Remove([A1] uint node) => ExecList.Remove(_memory, node);

    [LibraryFunctionOffset(-258)]
    public uint RemHead([A0] uint list) => ExecList.RemoveHead(_memory, list);

    [LibraryFunctionOffset(-264)]
    public uint RemTail([A0] uint list) => ExecList.RemoveTail(_memory, list);

    [LibraryFunctionOffset(-270)]
    public void Enqueue([A0] uint list, [A1] uint node) => ExecList.Enqueue(_memory, list, node);

    [LibraryFunctionOffset(-276)]
    public uint FindName([A0] uint start, [A1] uint name) => ExecList.FindName(_memory, start, _memory.ReadCString(name));

    // task = FindTask(name)
    // D0              A1
    [LibraryFunctionOffset(-294)]
    public uint FindTask([A1] uint name)
    {
        if (name == 0)
            return ThisTask;
        var wanted = _memory.ReadCString(name);
        return core.Scheduler.Tasks.FirstOrDefault(task =>
        {
            var taskName = _memory.Read32(task + NodeOffsets.Name);
            return taskName != 0 && _memory.ReadCString(taskName) == wanted;
        });
    }

    // oldPriority = SetTaskPri(task, priority)
    // D0                       A1    D0
    [LibraryFunctionOffset(-300)]
    public int SetTaskPri([A1] uint task, [D0] int priority)
    {
        var old = (sbyte)_memory.Read8(task + NodeOffsets.Priority);
        _memory.Write8(task + NodeOffsets.Priority, (byte)priority);
        return old;
    }

    // oldSignals = SetSignal(newSignals, signalMask)
    // D0                     D0          D1
    [LibraryFunctionOffset(-306)]
    public uint SetSignal([D0] uint newSignals, [D1] uint mask)
    {
        var address = ThisTask + TaskOffsets.SignalsReceived;
        var old = _memory.Read32(address);
        _memory.Write32(address, (old & ~mask) | (newSignals & mask));
        return old;
    }

    // signals = Wait(signalSet)
    // D0             D0
    [LibraryFunctionOffset(-318)]
    public uint Wait([D0] uint signals) => core.WaitForSignals(signals);

    // Signal(task, signals)
    //        A1    D0
    [LibraryFunctionOffset(-324)]
    public void Signal([A1] uint task, [D0] uint signals) => core.Signal(task, signals);

    // signalNum = AllocSignal(signalNum)
    // D0                      D0
    [LibraryFunctionOffset(-330)]
    public int AllocSignal([D0] int number)
    {
        var address = ThisTask + TaskOffsets.SignalsAllocated;
        var allocated = _memory.Read32(address);
        if (number == -1)
        {
            number = Enumerable.Range(0, 32).Reverse().FirstOrDefault(bit => (allocated & (1u << bit)) == 0, -1);
            if (number == -1)
                return -1;
        }
        else if ((allocated & (1u << number)) != 0)
        {
            return -1;
        }

        _memory.Write32(address, allocated | (1u << number));
        var received = ThisTask + TaskOffsets.SignalsReceived;
        _memory.Write32(received, _memory.Read32(received) & ~(1u << number));
        return number;
    }

    // FreeSignal(signalNum)
    //            D0
    [LibraryFunctionOffset(-336)]
    public void FreeSignal([D0] int number)
    {
        if (number < 0)
            return;
        var address = ThisTask + TaskOffsets.SignalsAllocated;
        _memory.Write32(address, _memory.Read32(address) & ~(1u << number));
    }

    // trapNum = AllocTrap(trapNum)
    // D0                  D0
    [LibraryFunctionOffset(-342)]
    public int AllocTrap([D0] int number)
    {
        var address = ThisTask + TaskOffsets.TrapsAllocated;
        var allocated = _memory.Read16(address);
        if (number == -1)
            number = Enumerable.Range(0, 16).FirstOrDefault(bit => (allocated & (1 << bit)) == 0, -1);
        if (number < 0 || (allocated & (1 << number)) != 0)
            return -1;
        _memory.Write16(address, (ushort)(allocated | (1 << number)));
        return number;
    }

    // FreeTrap(trapNum)
    //          D0
    [LibraryFunctionOffset(-348)]
    public void FreeTrap([D0] int number)
    {
        var address = ThisTask + TaskOffsets.TrapsAllocated;
        _memory.Write16(address, (ushort)(_memory.Read16(address) & ~(1 << number)));
    }

    // AddPort(port)
    //         A1
    [LibraryFunctionOffset(-354)]
    public void AddPort([A1] uint port)
    {
        _memory.Write8(port + NodeOffsets.Type, NodeType.MsgPort);
        ExecList.Initialize(_memory, port + MsgPortOffsets.MessageList, NodeType.Message);
        ExecList.Enqueue(_memory, Base + ExecBaseOffsets.PortList, port);
    }

    // RemPort(port)
    //         A1
    [LibraryFunctionOffset(-360)]
    public void RemPort([A1] uint port) => ExecList.Remove(_memory, port);

    // PutMsg(port, message)
    //        A0    A1
    [LibraryFunctionOffset(-366)]
    public void PutMsg([A0] uint port, [A1] uint message)
    {
        _memory.Write8(message + NodeOffsets.Type, NodeType.Message);
        core.DeliverMessage(port, message);
    }

    // message = GetMsg(port)
    // D0               A0
    [LibraryFunctionOffset(-372)]
    public uint GetMsg([A0] uint port) => ExecList.RemoveHead(_memory, port + MsgPortOffsets.MessageList);

    // ReplyMsg(message)
    //          A1
    [LibraryFunctionOffset(-378)]
    public void ReplyMsg([A1] uint message)
    {
        var replyPort = _memory.Read32(message + MessageOffsets.ReplyPort);
        if (replyPort == 0)
        {
            _memory.Write8(message + NodeOffsets.Type, NodeType.FreeMessage);
            return;
        }

        _memory.Write8(message + NodeOffsets.Type, NodeType.ReplyMessage);
        core.DeliverMessage(replyPort, message);
    }

    // message = WaitPort(port)
    // D0                 A0
    [LibraryFunctionOffset(-384)]
    public uint WaitPort([A0] uint port)
    {
        var list = port + MsgPortOffsets.MessageList;
        while (ExecList.IsEmpty(_memory, list))
            core.WaitForSignals(1u << _memory.Read8(port + MsgPortOffsets.SignalBit));
        return ExecList.Head(_memory, list);
    }

    // port = FindPort(name)
    // D0              A1
    [LibraryFunctionOffset(-390)]
    public uint FindPort([A1] uint name) =>
        ExecList.FindName(_memory, Base + ExecBaseOffsets.PortList, _memory.ReadCString(name));

    // library = OldOpenLibrary(libName)
    // D0                       A1
    [LibraryFunctionOffset(-408)]
    public uint OldOpenLibrary([A1] uint name) => OpenLibrary(name, 0);

    // CloseLibrary(library)
    //              A1
    // void CloseLibrary(struct Library *);
    [LibraryFunctionOffset(-414)]
    public void CloseLibrary([A1] uint library)
    {
        // CloseLibrary accepts NULL since V36.
        if (library != 0)
            core.Libraries.Close(library);
    }

    // oldFunc = SetFunction(library, funcOffset, newFunction)
    // D0                    A1       A0.W        D0
    [LibraryFunctionOffset(-420)]
    public uint SetFunction([A1] uint library, [A0] uint offset, [D0] uint function) =>
        core.Libraries.SetFunction(library, (short)offset, function);

    // error = OpenDevice(devName, unitNumber, iORequest, flags)
    // D0                 A0       D0          A1         D1
    [LibraryFunctionOffset(-444)]
    public int OpenDevice([A0] uint name, [D0] uint unit, [A1] uint request, [D1] uint flags)
    {
        var deviceName = _memory.ReadCString(name);
        var error = core.Devices.Open(deviceName, unit, request, flags);
        if (error != 0)
            _memory.Write8(request + IoRequestOffsets.Error, (byte)error);
        return error;
    }

    // CloseDevice(iORequest)
    //             A1
    [LibraryFunctionOffset(-450)]
    public void CloseDevice([A1] uint request) => core.Devices.Close(request);

    // error = DoIO(iORequest)
    // D0           A1
    [LibraryFunctionOffset(-456)]
    public int DoIO([A1] uint request)
    {
        _memory.Write8(request + IoRequestOffsets.Flags, IoRequestOffsets.FlagQuick);
        core.Devices.BeginIO(request);
        return WaitIO(request);
    }

    // SendIO(iORequest)
    //        A1
    [LibraryFunctionOffset(-462)]
    public void SendIO([A1] uint request)
    {
        _memory.Write8(request + IoRequestOffsets.Flags, 0);
        core.Devices.BeginIO(request);
    }

    // result = CheckIO(iORequest)
    // D0               A1
    [LibraryFunctionOffset(-468)]
    public uint CheckIO([A1] uint request) => core.Devices.IsComplete(request) ? request : 0;

    // error = WaitIO(iORequest)
    // D0             A1
    [LibraryFunctionOffset(-474)]
    public int WaitIO([A1] uint request)
    {
        while (!core.Devices.IsComplete(request))
            core.WaitForSignals(core.Devices.ReplySignal(request));

        // WaitIO removes the request from the reply port if it is there.
        if (_memory.Read8(request + NodeOffsets.Type) == NodeType.ReplyMessage
            && (_memory.Read8(request + IoRequestOffsets.Flags) & IoRequestOffsets.FlagQuick) == 0)
            ExecList.Remove(_memory, request);
        return (sbyte)_memory.Read8(request + IoRequestOffsets.Error);
    }

    // error = AbortIO(iORequest)
    // D0              A1
    [LibraryFunctionOffset(-480)]
    public int AbortIO([A1] uint request) => core.Devices.Abort(request);

    // resource = OpenResource(resName)
    // D0                      A1
    [LibraryFunctionOffset(-498)]
    public uint OpenResource([A1] uint name) => core.Libraries.OpenResource(_memory.ReadCString(name));

    // NextData = RawDoFmt(FormatString, DataStream, PutChProc, PutChData)
    // D0                  A0            A1          A2         A3
    [LibraryFunctionOffset(-522)]
    public uint RawDoFmt([A0] uint format, [A1] uint data, [A2] uint putCharacter, [A3] uint putData)
    {
        var (text, next) = RawDoFormat.Format(_memory, format, data);
        var cpu = core.Cpu;
        // PutChProc gets each character in D0, and the end of the text as a zero. A3 is the same for each call, so
        // a procedure such as "MOVE.B D0,(A3)+" can move it.
        cpu.A[3] = putData;
        foreach (var character in text.Append((byte)0))
        {
            cpu.D[0] = character;
            if (putCharacter != 0)
                core.CallFromNative(putCharacter);
        }

        return next;
    }

    // oldBits = SetSR(newSR, mask)
    // D0              D0     D1
    [LibraryFunctionOffset(-144)]
    public uint SetSR([D0] uint newBits, [D1] uint mask)
    {
        var cpu = core.Cpu;
        var old = cpu.Sr;
        cpu.Sr = (ushort)((old & ~mask) | (newBits & mask));
        return old;
    }

    // condition = GetCC()
    // D0
    [LibraryFunctionOffset(-528)]
    public uint GetCC() => core.Cpu.Ccr;

    // type = TypeOfMem(address)
    // D0               A1
    [LibraryFunctionOffset(-534)]
    public uint TypeOfMem([A1] uint address) => (uint)core.Allocator.TypeOf(address);

    // library = OpenLibrary(libName, version)
    // D0                    A1       D0
    // struct Library *OpenLibrary(STRPTR, ULONG);
    [LibraryFunctionOffset(-552)]
    public uint OpenLibrary([A1] uint name, [D0] uint version) =>
        core.Libraries.Open(_memory.ReadCString(name), version)?.Base ?? 0;

    // CopyMem(source, dest, size)
    //         A0      A1    D0
    [LibraryFunctionOffset(-624)]
    public void CopyMem([A0] uint source, [A1] uint destination, [D0] uint size) =>
        _memory.WriteBytes(destination, _memory.ReadBytes(source, (int)size));

    // CopyMemQuick(source, dest, size)
    //              A0      A1    D0
    [LibraryFunctionOffset(-630)]
    public void CopyMemQuick([A0] uint source, [A1] uint destination, [D0] uint size) =>
        CopyMem(source, destination, size);

    // The 68000 has no caches.
    [LibraryFunctionOffset(-636)]
    public void CacheClearU()
    {
    }

    [LibraryFunctionOffset(-642)]
    public void CacheClearE([A0] uint address, [D0] uint length, [D1] uint caches)
    {
    }

    // oldBits = CacheControl(cacheBits, cacheMask)
    // D0                     D0         D1
    [LibraryFunctionOffset(-648)]
    public uint CacheControl([D0] uint bits, [D1] uint mask) => 0;

    // ioReq = CreateIORequest(ioReplyPort, size)
    // D0                      A0           D0
    [LibraryFunctionOffset(-654)]
    public uint CreateIORequest([A0] uint replyPort, [D0] uint size)
    {
        if (replyPort == 0)
            return 0;
        var request = core.Allocator.Allocate(size, MemoryFlags.Public | MemoryFlags.Clear);
        if (request == 0)
            return 0;
        _memory.Write8(request + NodeOffsets.Type, NodeType.ReplyMessage);
        _memory.Write32(request + MessageOffsets.ReplyPort, replyPort);
        _memory.Write16(request + MessageOffsets.Length, (ushort)size);
        return request;
    }

    // DeleteIORequest(ioReq)
    //                 A0
    [LibraryFunctionOffset(-660)]
    public void DeleteIORequest([A0] uint request)
    {
        if (request != 0)
            core.Allocator.Free(request, _memory.Read16(request + MessageOffsets.Length));
    }

    // port = CreateMsgPort()
    // D0
    [LibraryFunctionOffset(-666)]
    public uint CreateMsgPort()
    {
        var signal = AllocSignal(-1);
        if (signal == -1)
            return 0;
        var port = core.Allocator.Allocate(MsgPortOffsets.Size, MemoryFlags.Public | MemoryFlags.Clear);
        if (port == 0)
        {
            FreeSignal(signal);
            return 0;
        }

        _memory.Write8(port + NodeOffsets.Type, NodeType.MsgPort);
        _memory.Write8(port + MsgPortOffsets.Flags, MsgPortOffsets.PaSignal);
        _memory.Write8(port + MsgPortOffsets.SignalBit, (byte)signal);
        _memory.Write32(port + MsgPortOffsets.SignalTask, ThisTask);
        ExecList.Initialize(_memory, port + MsgPortOffsets.MessageList, NodeType.Message);
        return port;
    }

    // DeleteMsgPort(port)
    //               A0
    [LibraryFunctionOffset(-672)]
    public void DeleteMsgPort([A0] uint port)
    {
        if (port == 0)
            return;
        FreeSignal(_memory.Read8(port + MsgPortOffsets.SignalBit));
        core.Allocator.Free(port, MsgPortOffsets.Size);
    }

    // memoryBlock = AllocVec(byteSize, attributes)
    // D0                     D0        D1
    [LibraryFunctionOffset(-684)]
    public uint AllocVec([D0] uint size, [D1] uint attributes)
    {
        // AllocVec keeps the size in the long before the block.
        var block = core.Allocator.Allocate(size + 4, (MemoryFlags)attributes);
        if (block == 0)
            return 0;
        _memory.Write32(block, size + 4);
        return block + 4;
    }

    // FreeVec(memoryBlock)
    //         A1
    [LibraryFunctionOffset(-690)]
    public void FreeVec([A1] uint block)
    {
        if (block != 0)
            core.Allocator.Free(block - 4, _memory.Read32(block - 4));
    }

    // ColdReboot()
    [LibraryFunctionOffset(-726)]
    public void ColdReboot() => throw new ColdRebootException();

    // Alert(alertNum)
    //       D7
    [LibraryFunctionOffset(-108)]
    public void Alert([D7] uint number) => throw new AlertException(number);

    private void ChangeNestCount(uint offset, int change)
    {
        var address = Base + offset;
        _memory.Write8(address, (byte)((sbyte)_memory.Read8(address) + change));
    }

    private uint InterruptVector(int number)
    {
        if (number is < 0 or >= InterruptCount)
            throw new ArgumentOutOfRangeException(nameof(number), $"Interrupt {number} does not exist.");
        return Base + ExecBaseOffsets.InterruptVectors + (uint)number * InterruptVectorSize;
    }

    private uint ServerChain(int number)
    {
        if (!ServerChains.Contains(number))
            throw new InvalidOperationException($"Interrupt {number} has no server chain.");
        return _memory.Read32(InterruptVector(number));
    }
}
