namespace AmigaSharp.Runtime.Exec;

// Field offsets of the exec and dos structures, from the V40 include files. Programs read these structures
// directly, so the runtime keeps them in the emulated memory.

/// <summary><c>struct Node</c> (exec/nodes.h).</summary>
public static class NodeOffsets
{
    public const uint Successor = 0;
    public const uint Predecessor = 4;
    public const uint Type = 8;
    public const uint Priority = 9;
    public const uint Name = 10;
    public const uint Size = 14;
}

/// <summary>The node types (ln_Type).</summary>
public static class NodeType
{
    public const byte Task = 1;
    public const byte Interrupt = 2;
    public const byte Device = 3;
    public const byte MsgPort = 4;
    public const byte Message = 5;
    public const byte FreeMessage = 6;
    public const byte ReplyMessage = 7;
    public const byte Resource = 8;
    public const byte Library = 9;
    public const byte Memory = 10;
    public const byte Process = 13;
}

/// <summary><c>struct List</c> (exec/lists.h).</summary>
public static class ListOffsets
{
    public const uint Head = 0;
    public const uint Tail = 4;
    public const uint TailPredecessor = 8;
    public const uint Type = 12;
    public const uint Size = 14;
}

/// <summary><c>struct Task</c> (exec/tasks.h).</summary>
public static class TaskOffsets
{
    public const uint Flags = 14;
    public const uint State = 15;
    public const uint InterruptDisableCount = 16;
    public const uint TaskDisableCount = 17;
    public const uint SignalsAllocated = 18;
    public const uint SignalsWaited = 22;
    public const uint SignalsReceived = 26;
    public const uint SignalsException = 30;
    public const uint TrapsAllocated = 34;
    public const uint StackPointer = 54;
    public const uint StackLower = 58;
    public const uint StackUpper = 62;
    public const uint MemEntry = 74;
    public const uint UserData = 88;
    public const uint Size = 92;

    public const byte StateRunning = 2;
}

/// <summary><c>struct Process</c> (dos/dosextens.h). A process starts with a task.</summary>
public static class ProcessOffsets
{
    public const uint MsgPort = 92;
    public const uint SegList = 128;
    public const uint StackSize = 132;
    public const uint TaskNumber = 140;
    public const uint StackBase = 144;
    public const uint Result2 = 148;
    public const uint CurrentDir = 152;
    public const uint InputStream = 156;
    public const uint OutputStream = 160;
    public const uint ConsoleTask = 164;
    public const uint FileSystemTask = 168;
    public const uint Cli = 172;
    public const uint WindowPtr = 184;
    public const uint HomeDir = 188;
    public const uint Flags = 192;
    public const uint Arguments = 204;
    public const uint LocalVars = 208;
    public const uint ErrorStream = 224;
    public const uint Size = 228;
}

/// <summary><c>struct CommandLineInterface</c> (dos/dosextens.h). The strings are BSTRs.</summary>
public static class CliOffsets
{
    public const uint Result2 = 0;
    public const uint SetName = 4;
    public const uint CommandDir = 8;
    public const uint ReturnCode = 12;
    public const uint CommandName = 16;
    public const uint FailLevel = 20;
    public const uint Prompt = 24;
    public const uint StandardInput = 28;
    public const uint CurrentInput = 32;
    public const uint CommandFile = 36;
    public const uint Interactive = 40;
    public const uint Background = 44;
    public const uint CurrentOutput = 48;
    public const uint DefaultStack = 52;
    public const uint StandardOutput = 56;
    public const uint Module = 60;
    public const uint Size = 64;
}

/// <summary><c>struct MsgPort</c> (exec/ports.h).</summary>
public static class MsgPortOffsets
{
    public const uint Flags = 14;
    public const uint SignalBit = 15;
    public const uint SignalTask = 16;
    public const uint MessageList = 20;
    public const uint Size = 34;

    /// <summary>mp_Flags: signal the task when a message arrives.</summary>
    public const byte PaSignal = 0;
}

/// <summary><c>struct Message</c> (exec/ports.h).</summary>
public static class MessageOffsets
{
    public const uint ReplyPort = 14;
    public const uint Length = 18;
    public const uint Size = 20;
}

/// <summary><c>struct ExecBase</c> (exec/execbase.h).</summary>
/// <summary><c>struct MemHeader</c> (exec/memory.h): a region of memory in the MemList of ExecBase.</summary>
public static class MemHeaderOffsets
{
    public const uint Attributes = 14;
    public const uint First = 16;
    public const uint Lower = 20;
    public const uint Upper = 24;
    public const uint Free = 28;
    public const uint Size = 32;
}

public static class ExecBaseOffsets
{
    public const uint SoftVersion = 34;
    public const uint MaxLocMem = 62;
    public const uint MaxExtMem = 78;
    public const uint InterruptVectors = 84;
    public const uint ThisTask = 276;
    public const uint IdleCount = 280;
    public const uint DispatchCount = 284;
    public const uint Quantum = 288;
    public const uint InterruptDisableCount = 294;
    public const uint TaskDisableCount = 295;
    public const uint AttnFlags = 296;
    public const uint TaskSignalsAllocated = 316;
    public const uint TaskTrapsAllocated = 320;
    public const uint MemList = 322;
    public const uint ResourceList = 336;
    public const uint DeviceList = 350;
    public const uint InterruptList = 364;
    public const uint LibraryList = 378;
    public const uint PortList = 392;
    public const uint TaskReady = 406;
    public const uint TaskWait = 420;
    public const uint VBlankFrequency = 530;
    public const uint PowerSupplyFrequency = 531;
    public const uint SemaphoreList = 532;
    public const uint EClockFrequency = 568;
    public const uint Size = 632;
}

/// <summary>
/// The list operations of exec (Insert, AddHead, AddTail, Remove, RemHead, RemTail, Enqueue, FindName) on lists in
/// the emulated memory.
/// </summary>
public static class ExecList
{
    /// <summary>NEWLIST: makes an empty list.</summary>
    public static void Initialize(Memory memory, uint list, byte type = 0)
    {
        memory.Write32(list + ListOffsets.Head, list + ListOffsets.Tail);
        memory.Write32(list + ListOffsets.Tail, 0);
        memory.Write32(list + ListOffsets.TailPredecessor, list + ListOffsets.Head);
        memory.Write8(list + ListOffsets.Type, type);
    }

    public static bool IsEmpty(Memory memory, uint list) =>
        memory.Read32(list + ListOffsets.TailPredecessor) == list + ListOffsets.Head;

    public static uint Head(Memory memory, uint list)
    {
        var head = memory.Read32(list + ListOffsets.Head);
        return memory.Read32(head + NodeOffsets.Successor) == 0 ? 0 : head;
    }

    /// <summary>Insert: puts the node after <paramref name="predecessor"/>. A predecessor of 0 means the head of the list.</summary>
    public static void Insert(Memory memory, uint list, uint node, uint predecessor)
    {
        if (predecessor == 0)
            predecessor = list + ListOffsets.Head;
        var successor = memory.Read32(predecessor + NodeOffsets.Successor);
        memory.Write32(node + NodeOffsets.Successor, successor);
        memory.Write32(node + NodeOffsets.Predecessor, predecessor);
        memory.Write32(successor + NodeOffsets.Predecessor, node);
        memory.Write32(predecessor + NodeOffsets.Successor, node);
    }

    public static void AddHead(Memory memory, uint list, uint node) => Insert(memory, list, node, 0);

    public static void AddTail(Memory memory, uint list, uint node) =>
        Insert(memory, list, node, memory.Read32(list + ListOffsets.TailPredecessor));

    public static void Remove(Memory memory, uint node)
    {
        var successor = memory.Read32(node + NodeOffsets.Successor);
        var predecessor = memory.Read32(node + NodeOffsets.Predecessor);
        memory.Write32(predecessor + NodeOffsets.Successor, successor);
        memory.Write32(successor + NodeOffsets.Predecessor, predecessor);
    }

    /// <summary>RemHead: removes the first node. Returns 0 if the list is empty.</summary>
    public static uint RemoveHead(Memory memory, uint list)
    {
        var node = Head(memory, list);
        if (node != 0)
            Remove(memory, node);
        return node;
    }

    /// <summary>RemTail: removes the last node. Returns 0 if the list is empty.</summary>
    public static uint RemoveTail(Memory memory, uint list)
    {
        if (IsEmpty(memory, list))
            return 0;
        var node = memory.Read32(list + ListOffsets.TailPredecessor);
        Remove(memory, node);
        return node;
    }

    /// <summary>Enqueue: puts the node before the first node that has a lower priority.</summary>
    public static void Enqueue(Memory memory, uint list, uint node)
    {
        var priority = (sbyte)memory.Read8(node + NodeOffsets.Priority);
        var current = memory.Read32(list + ListOffsets.Head);
        while (memory.Read32(current + NodeOffsets.Successor) != 0
               && (sbyte)memory.Read8(current + NodeOffsets.Priority) >= priority)
            current = memory.Read32(current + NodeOffsets.Successor);
        Insert(memory, list, node, memory.Read32(current + NodeOffsets.Predecessor));
    }

    /// <summary>
    /// FindName: finds the first node after <paramref name="start"/> that has the name. The start is a list or a node.
    /// </summary>
    public static uint FindName(Memory memory, uint start, string name)
    {
        for (var node = memory.Read32(start + NodeOffsets.Successor);
             node != 0 && memory.Read32(node + NodeOffsets.Successor) != 0;
             node = memory.Read32(node + NodeOffsets.Successor))
        {
            var namePointer = memory.Read32(node + NodeOffsets.Name);
            if (namePointer != 0 && memory.ReadCString(namePointer) == name)
                return node;
        }

        return 0;
    }

    /// <summary>The nodes of the list, from head to tail.</summary>
    public static IEnumerable<uint> Nodes(Memory memory, uint list)
    {
        for (var node = memory.Read32(list + ListOffsets.Head);
             memory.Read32(node + NodeOffsets.Successor) != 0;
             node = memory.Read32(node + NodeOffsets.Successor))
            yield return node;
    }
}

/// <summary><c>struct Interrupt</c> (exec/interrupts.h).</summary>
public static class InterruptOffsets
{
    public const uint Data = 14;
    public const uint Code = 18;
    public const uint Size = 22;
}

/// <summary><c>struct IORequest</c> and <c>struct IOStdReq</c> (exec/io.h).</summary>
public static class IoRequestOffsets
{
    public const uint Device = 20;
    public const uint Unit = 24;
    public const uint Command = 28;
    public const uint Flags = 30;
    public const uint Error = 31;
    public const uint Actual = 32;
    public const uint Length = 36;
    public const uint Data = 40;
    public const uint Offset = 44;
    public const uint Size = 32;
    public const uint StandardSize = 48;

    /// <summary>IOF_QUICK: the caller wants the request to complete at once if possible.</summary>
    public const byte FlagQuick = 1;

    /// <summary>IOERR_OPENFAIL: the device or the unit cannot open.</summary>
    public const int ErrorOpenFail = -1;

    /// <summary>IOERR_ABORTED.</summary>
    public const int ErrorAborted = -2;

    /// <summary>IOERR_NOCMD: the device does not know the command.</summary>
    public const int ErrorNoCommand = -3;
}
