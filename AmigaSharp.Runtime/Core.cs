using System.Text;
using AmigaSharp.Runtime.Cpu;
using AmigaSharp.Runtime.Dos;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Hardware;
using AmigaSharp.Runtime.Libraries;
using AmigaSharp.Runtime.Libraries.Native;

namespace AmigaSharp.Runtime;

/// <summary>
/// One emulated Amiga: the memory, the CPU state, the HLE libraries and the process that runs the program.
/// </summary>
/// <remarks>
/// The memory map is the map of an Amiga 2000 with 2 MB of chip RAM and 8 MB of Zorro II fast RAM.
/// </remarks>
public sealed class Core
{
    /// <summary>Address 4 holds the pointer to ExecBase.</summary>
    public const uint SysBaseAddress = 4;

    /// <summary>
    /// The return address of the program. The entry point returns to this address when the program ends.
    /// No code is at this address.
    /// </summary>
    public const uint ExitAddress = 0x00FF_FFF0;

    // The exception vectors and the other low memory use $000 to $FFF.
    public const uint ChipStart = 0x1000;
    public const uint ChipEnd = 0x20_0000;
    public const uint FastStart = 0x20_0000;
    public const uint FastEnd = 0xA0_0000;

    /// <summary>
    /// The return addresses of calls from native code to 68000 code. Each level of nesting has its own address.
    /// No code is at these addresses.
    /// </summary>
    public const uint NativeReturnAddress = 0x00FF_FF00;

    private const uint SupervisorStackSize = 8 * 1024;

    /// <summary>The size of the stack of the main process. AmigaDOS gives it to the program at 4(SP).</summary>
    public const uint StackSize = 64 * 1024;

    // Poll checks the interrupts after this number of calls, so that a call at each backward branch costs little.
    private const int PollInterval = 64;

    // The translated functions by address, with the first 8 bytes of their code when they were registered.
    private readonly Dictionary<uint, (Action Function, uint First, uint Second)> _functions = new();
    private readonly Dictionary<uint, ProcessMemory> _processMemory = new();
    private readonly List<Func<bool>> _idleHandlers = [];
    private readonly List<Action> _pollHandlers = [];
    private int _nativeCallDepth;
    private int _pollCountdown = PollInterval;

    public Memory Memory { get; } = new();
    public CpuState Cpu { get; }

    /// <summary>Runs the code that has no translated function. A 68000 exception in that code is fatal.</summary>
    public Interpreter Interpreter { get; }

    /// <summary>The free memory of exec.</summary>
    public MemoryAllocator Allocator { get; }

    public LibraryManager Libraries { get; }

    public DeviceManager Devices { get; }

    public FileSystem FileSystem { get; }

    /// <summary>The custom chips and the CIAs.</summary>
    public Chipset Chipset { get; }

    public InterruptDispatcher Interrupts { get; }

    public Scheduler Scheduler { get; }

    /// <summary>The stream that the console output goes to.</summary>
    public Stream Output { get; }

    /// <summary>The stream that the console input comes from.</summary>
    public Stream Input { get; }

    /// <summary>The events of the host keyboard. input.device sends them to the handlers of the program.</summary>
    public Input.InputQueue KeyboardInput { get; } = new();

    /// <summary>Messages from the runtime about functions and devices that it does not have.</summary>
    public TextWriter Log { get; set; } = Console.Error;

    /// <summary>If true, the runtime writes each call to a native library function to <see cref="Log"/>.</summary>
    public bool TraceLibraryCalls { get; set; }

    /// <summary>The address of ExecBase.</summary>
    public uint ExecBase { get; }

    /// <summary>The address of the <c>struct Process</c> of the main process. ExecBase.ThisTask points to it.</summary>
    public uint MainProcess { get; }

    /// <summary>The address that the last RTS, RTR or RTE returned to.</summary>
    public uint LastReturnAddress { get; set; }

    private DateTime _startDate = DateTime.Now;

    /// <summary>
    /// The date and the time of the emulated Amiga: the start date plus the time of the hardware clock. So a virtual
    /// clock also moves the date. DateStamp and battclock.resource use it.
    /// </summary>
    public DateTime Now => _startDate + Chipset.Beam.Clock.Elapsed;

    /// <summary>Sets the date and the time of the emulated Amiga, for example the date of saved listing data.</summary>
    public void SetDate(DateTime date) => _startDate = date - Chipset.Beam.Clock.Elapsed;

    /// <param name="output">The console output. The default is the standard output of the host.</param>
    /// <param name="input">The console input. The default is the standard input of the host.</param>
    /// <param name="rootDirectory">The host directory of the volume SYS:. The default is the current directory.</param>
    /// <param name="clock">The time of the hardware. The default is real time.</param>
    public Core(Stream? output = null, Stream? input = null, string? rootDirectory = null, IClock? clock = null)
    {
        Chipset = new Chipset(clock ?? new RealTimeClock(), Memory);
        KeyboardInput.RawKeyPosted = Chipset.Keyboard.Post;
        Chipset.Now = () => Now;
        // Kickstart makes the drive lines of CIA-B port B outputs, with all motors off and no drive selected, and it
        // makes OVL and the power LED of CIA-A port A outputs.
        Chipset.CiaB.Write(Hardware.CiaRegister.Prb, 0xFF);
        Chipset.CiaB.Write(Hardware.CiaRegister.Ddrb, 0xFF);
        Chipset.CiaA.Write(Hardware.CiaRegister.Ddra, 0x03);
        Memory.Hardware = Chipset;
        Cpu = new CpuState(Memory);
        Interpreter = new Interpreter(Cpu) { ExceptionsAreFatal = true };
        Output = output ?? Console.OpenStandardOutput();
        Input = input ?? Console.OpenStandardInput();

        Allocator = new MemoryAllocator(Memory);
        Allocator.AddRegion(ChipStart, ChipEnd, isChip: true);
        Allocator.AddRegion(FastStart, FastEnd, isChip: false);

        Libraries = new LibraryManager(this);
        Devices = new DeviceManager(this);
        Interrupts = new InterruptDispatcher(this);
        // Kickstart enables the master bit and the interrupts of the CIAs, the vertical blank and the software.
        Chipset.Custom.Write(CustomRegister.Intena,
            0x8000 | 1 << InterruptBit.Enable | 1 << InterruptBit.Ports | 1 << InterruptBit.VerticalBlank
            | 1 << InterruptBit.External | 1 << InterruptBit.Soft);
        Libraries.Register("exec.library", core => new ExecLibrary(core));
        Libraries.Register("dos.library", core => new DosLibrary(core));
        Libraries.Register("graphics.library", core => new GraphicsLibrary(core));
        Libraries.Register("diskfont.library", core => new DiskFontLibrary(core));
        Libraries.Register("intuition.library", core => new IntuitionLibrary(core));
        Libraries.Register("utility.library", core => new UtilityLibrary(core));
        Libraries.RegisterResource("battclock.resource", core => new BattClockResource(core));
        Libraries.Register("serial.device", core => new SerialDevice(core));
        Libraries.Register("input.device", core => new InputDevice(core));
        Libraries.Register("console.device", core => new ConsoleDevice(core));
        Libraries.Register("trackdisk.device", core => new TrackDiskDevice(core));

        // Exec is always open.
        ExecBase = Libraries.Open("exec.library", 0)!.Base;
        Memory.Write32(SysBaseAddress, ExecBase);

        FileSystem = new FileSystem(this, rootDirectory ?? Directory.GetCurrentDirectory());
        MainProcess = CreateProcess("AmigaSharp", 0, StackSize);
        Memory.Write32(MainProcess + ProcessOffsets.InputStream, FileSystem.OpenConsole());
        Memory.Write32(MainProcess + ProcessOffsets.OutputStream, FileSystem.OpenConsole());
        Cpu.Sp = Memory.Read32(MainProcess + TaskOffsets.StackUpper);
        Memory.Write32(ExecBase + ExecBaseOffsets.ThisTask, MainProcess);
        Scheduler = new Scheduler(this, MainProcess);
        Cpu.Ssp = AllocateSystem(SupervisorStackSize) + SupervisorStackSize;
    }

    /// <summary>
    /// Allocates memory for the runtime, from the top of fast memory. The program hunks load at fixed addresses at the
    /// bottom of each memory region, so this memory does not overlap them. The memory is clear.
    /// </summary>
    public uint AllocateSystem(uint size, MemoryFlags flags = MemoryFlags.Any)
    {
        var address = Allocator.Allocate(size, flags | MemoryFlags.Public | MemoryFlags.Clear | MemoryFlags.Reverse);
        if (address == 0)
            throw new OutOfMemoryException($"The runtime has no memory for {size} bytes.");
        return address;
    }

    /// <summary>Allocates memory for the runtime and copies the bytes to it.</summary>
    public uint AllocateSystem(ReadOnlySpan<byte> bytes)
    {
        var address = AllocateSystem((uint)Math.Max(bytes.Length, 1));
        Memory.WriteBytes(address, bytes);
        return address;
    }

    public void FreeSystem(uint address, uint size) => Allocator.Free(address, size);

    /// <summary>
    /// Opens a library by name. Returns null if the library does not exist or if its version is less than the minimum.
    /// </summary>
    public AbstractLibrary? OpenLibrary(string name, uint minimumVersion) => Libraries.Open(name, minimumVersion);

    public void RegisterLibrary(string name, Func<Core, AbstractLibrary> factory) => Libraries.Register(name, factory);

    /// <summary>Calls the library function at the offset from the library base, as <c>JSR offset(A6)</c> does.</summary>
    public void CallVector(uint libraryBase, short offset)
    {
        CallAddress(ExitAddress, libraryBase + (uint)offset);
    }

    /// <summary>
    /// Makes a translated function the code at the address. The code must be in memory: the function is used only
    /// while the first bytes of the code stay the same.
    /// </summary>
    public void RegisterFunction(uint address, Action function)
    {
        address &= Memory.AddressMask;
        _functions[address] = (function, Memory.Read32(address), Memory.Read32(address + 4));
    }

    /// <summary>
    /// Gets the translated function at the address. A program can write new code over its code, for example the
    /// decruncher of a packed program. If the first bytes changed, the translation is old: the method removes it, and
    /// the interpreter then runs the new code.
    /// </summary>
    private bool TryGetFunction(uint address, out Action function)
    {
        if (_functions.TryGetValue(address, out var entry))
        {
            if (Memory.Read32(address) == entry.First && Memory.Read32(address + 4) == entry.Second)
            {
                function = entry.Function;
                return true;
            }

            _functions.Remove(address);
        }

        function = null!;
        return false;
    }

    /// <summary>
    /// Does <c>JSR</c> to a translated function: pushes the return address and calls the function.
    /// </summary>
    /// <exception cref="StackUnwindException">
    /// The code returned to a different address. A frame further up the C# call stack continues at that address.
    /// </exception>
    public void Call(uint returnAddress, Action function)
    {
        Cpu.Push32(returnAddress);
        var slot = Cpu.Sp;
        try
        {
            function();
            ContinueAfterJumpReturns(returnAddress, slot);
        }
        catch (StackUnwindException unwind) when (unwind.ReturnAddress == returnAddress)
        {
            return;
        }

        if (LastReturnAddress != returnAddress)
            throw new StackUnwindException(LastReturnAddress);
    }

    /// <summary>
    /// Continues the code after an RTS that is a jump, until the code returns from the call. Some code jumps with
    /// <c>PEA target</c> and <c>RTS</c>, for example the decruncher of a packed program. The RTS then does not remove
    /// the return address of the call from the stack. If the stack still has the return address at its slot, the call
    /// did not return: the code continues at the address of the RTS. Else the code returned to a different call, and
    /// the caller unwinds the stack.
    /// </summary>
    private void ContinueAfterJumpReturns(uint returnAddress, uint slot)
    {
        while (LastReturnAddress != returnAddress && Cpu.Sp <= slot)
            Dispatch(LastReturnAddress);
    }

    /// <summary>Does <c>JSR</c> to an address that is known only at run time, for example <c>JSR -552(A6)</c>.</summary>
    public void CallAddress(uint returnAddress, uint target)
    {
        Call(returnAddress, () => Dispatch(target));
    }

    /// <summary>
    /// Continues at the address with the current stack, as <c>JMP</c> does. This method returns when that code
    /// returns from the current frame. The translated code must then return at once.
    /// </summary>
    public void Dispatch(uint target)
    {
        Poll();
        target &= Memory.AddressMask;

        // A library vector is a JMP to a stub or to a function that SetFunction installed.
        if (Memory.Read16(target) == 0x4EF9)
            target = Memory.Read32(target + 2) & Memory.AddressMask;

        if (Libraries.TryGetStub(target, out var native))
        {
            RunNative(target, native);
            LastReturnAddress = Cpu.Pop32();
            return;
        }

        if (TryGetFunction(target, out var function))
        {
            function();
            return;
        }

        RunInterpreted(target);
    }

    /// <summary>
    /// Calls 68000 code from a native function, for example the PutChProc of RawDoFmt. The code returns with RTS, or
    /// with RTE if <paramref name="supervisorFrame"/> is true. The call then runs in supervisor mode with an
    /// exception frame on the supervisor stack, as for exec Supervisor.
    /// </summary>
    public void CallFromNative(uint address, bool supervisorFrame = false)
    {
        var returnAddress = NativeReturnAddress - (uint)_nativeCallDepth * 2;
        _nativeCallDepth++;
        try
        {
            if (supervisorFrame)
            {
                var sr = Cpu.Sr;
                Cpu.SetSupervisor(true);
                Cpu.Push32(returnAddress);
                Cpu.Push16(sr);
            }
            else
            {
                Cpu.Push32(returnAddress);
            }

            var slot = Cpu.Sp + (supervisorFrame ? 2u : 0u);
            try
            {
                Dispatch(address);
                ContinueAfterJumpReturns(returnAddress, slot);
            }
            catch (StackUnwindException unwind) when (unwind.ReturnAddress == returnAddress)
            {
                return;
            }

            if (LastReturnAddress != returnAddress)
                throw new StackUnwindException(LastReturnAddress);
        }
        finally
        {
            _nativeCallDepth--;
        }
    }

    /// <summary>
    /// Adds a handler that the runtime calls while the program waits for signals. A device, a timer or an interrupt
    /// can send a signal from it. The handler returns true if it did something.
    /// </summary>
    public void AddIdleHandler(Func<bool> handler) => _idleHandlers.Add(handler);

    /// <summary>Adds a handler that the runtime calls at each safe point, for example to deliver input events.</summary>
    public void AddPollHandler(Action handler) => _pollHandlers.Add(handler);

    /// <summary>
    /// exec Wait: returns the signals in the mask that the task received, and clears them. While the task waits, the
    /// interrupts and the devices run, and they can send the signals.
    /// </summary>
    /// <exception cref="WaitDeadlockException">No interrupt, device or idle handler can send a signal.</exception>
    public uint WaitForSignals(uint mask)
    {
        var task = Memory.Read32(ExecBase + ExecBaseOffsets.ThisTask);
        var address = task + TaskOffsets.SignalsReceived;
        Memory.Write32(task + TaskOffsets.SignalsWaited, mask);
        try
        {
            while (true)
            {
                var received = Memory.Read32(address);
                if ((received & mask) != 0)
                {
                    Memory.Write32(address, received & ~mask);
                    return received & mask;
                }

                PollNow();
                if ((Memory.Read32(address) & mask) != 0)
                    continue;

                // Another task can run while this task waits.
                if (Scheduler.WaitForOtherTask(mask))
                    continue;

                var progress = false;
                foreach (var handler in _idleHandlers.ToList())
                    progress |= handler();
                if (progress)
                    continue;
                if (!Interrupts.CanInterrupt() && !Devices.HasPendingRequests && !Scheduler.OtherTaskCanRun)
                    throw new WaitDeadlockException(mask);

                var clock = Chipset.Beam.Clock;
                clock.WaitUntil(clock.Elapsed + IdleStep);
            }
        }
        finally
        {
            Scheduler.StopWaiting();
            Memory.Write32(task + TaskOffsets.SignalsWaited, 0);
        }
    }

    /// <summary>
    /// A safe point for interrupts. The translated code calls this method at each backward branch, so that a loop
    /// that waits for an interrupt ends.
    /// </summary>
    public void Poll()
    {
        if (--_pollCountdown > 0)
            return;
        _pollCountdown = PollInterval;
        Pace();
        PollNow();
    }

    /// <summary>Makes the interrupt requests that time causes, updates the devices, and delivers the interrupts.</summary>
    public void PollNow()
    {
        UpdateHardware();
        Scheduler?.Preempt();
    }

    private void UpdateHardware()
    {
        Chipset.Beam.Clock.Tick();
        Chipset.Custom.Update();
        Chipset.Audio.Update(Chipset.Beam.ColorClocks);
        Chipset.CiaA.Update();
        Chipset.CiaB.Update();
        Chipset.Keyboard.Update();
        Chipset.Disks.Update();
        Chipset.CtsLine.Update();
        Chipset.DsrLine.Update();
        Devices.Update();
        Interrupts.Deliver();
        foreach (var handler in _pollHandlers)
            handler();
    }

    /// <summary>
    /// Set to false to run the CPU as fast as the host can with a real-time clock. By default, the CPU runs at the
    /// speed of a 68000. The value can change while the program runs, for example to run the start of a program fast.
    /// </summary>
    public bool PaceCpu { get; set; } = true;

    // The CPU can be this far ahead of the clock before it sleeps, and this far behind before it stops to catch up.
    private static readonly long PaceAheadCycles = (long)(CycleEstimate.ClockHz * 0.001);
    private static readonly long PaceBehindCycles = (long)(CycleEstimate.ClockHz * 0.002);
    private static readonly TimeSpan PaceStep = TimeSpan.FromMicroseconds(250);
    private bool _pacing;
    private long _paceOffset;

    /// <summary>
    /// Runs the CPU at the speed of a 68000 with a real-time clock. If the estimated cycles of the CPU are ahead of the
    /// clock, the CPU sleeps until the clock is there. It wakes each 250 microseconds to deliver the interrupts, as a
    /// real CPU gets them while it runs. If the host is slower than a 68000, the CPU does not collect a debt of time:
    /// it is never more than 2 ms behind the clock.
    /// </summary>
    private void Pace()
    {
        var clock = Chipset.Beam.Clock;
        if (!clock.IsRealTime || _pacing)
            return;

        // The pacing compares the cycles since the last change of the offset with the clock. Cpu.Cycles itself only
        // grows, so it also shows how fast the CPU runs.
        var now = (long)(clock.Elapsed.TotalSeconds * CycleEstimate.ClockHz);
        var cycles = Cpu.Cycles - _paceOffset;
        if (!PaceCpu)
        {
            // The cycles follow the clock, so that the CPU does not sleep to pay back the time when the pacing starts.
            _paceOffset = Cpu.Cycles - now;
            return;
        }

        if (cycles < now - PaceBehindCycles)
        {
            _paceOffset = Cpu.Cycles - (now - PaceBehindCycles);
            return;
        }

        if (cycles <= now + PaceAheadCycles)
            return;

        // The interrupt code that runs while the CPU sleeps must not sleep again, and must not switch tasks.
        _pacing = true;
        try
        {
            var target = TimeSpan.FromSeconds(cycles / CycleEstimate.ClockHz);
            while (clock.Elapsed < target)
            {
                clock.WaitUntil(Min(target, clock.Elapsed + PaceStep));
                UpdateHardware();
            }
        }
        finally
        {
            _pacing = false;
        }
    }

    /// <summary>The state of the calls of a task that the scheduler saves when it switches tasks.</summary>
    public readonly record struct CallState(uint LastReturnAddress, int NativeCallDepth);

    /// <summary>True while native code calls 68000 code, for example a PutChProc of RawDoFmt.</summary>
    public bool InNativeCall => _nativeCallDepth > 0;

    public CallState SaveCallState() => new(LastReturnAddress, _nativeCallDepth);

    public void RestoreCallState(CallState state)
    {
        LastReturnAddress = state.LastReturnAddress;
        _nativeCallDepth = state.NativeCallDepth;
    }

    /// <summary>Waits for the start of the next frame, as graphics WaitTOF does.</summary>
    public void WaitForNextFrame()
    {
        var beam = Chipset.Beam;
        var next = beam.StartOfFrame(beam.Frame + 1);
        foreach (var handler in _idleHandlers.ToList())
            handler();
        while (beam.Clock.Elapsed < next)
        {
            PollNow();
            beam.Clock.WaitUntil(Min(next, beam.Clock.Elapsed + IdleStep));
        }

        PollNow();
    }

    /// <summary>The time that a wait sleeps before it checks the interrupts again.</summary>
    private static readonly TimeSpan IdleStep = TimeSpan.FromMilliseconds(1);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>exec Signal. The runtime has one task, so the signals go to it.</summary>
    public void Signal(uint task, uint signals)
    {
        var address = task + TaskOffsets.SignalsReceived;
        Memory.Write32(address, Memory.Read32(address) | signals);
        Scheduler.Signalled(task, signals);
    }

    /// <summary>Puts a message at the end of the message list of a port, and does the action of the port.</summary>
    public void DeliverMessage(uint port, uint message)
    {
        ExecList.AddTail(Memory, port + MsgPortOffsets.MessageList, message);
        switch (Memory.Read8(port + MsgPortOffsets.Flags) & 3)
        {
            case MsgPortOffsets.PaSignal:
                Signal(Memory.Read32(port + MsgPortOffsets.SignalTask), 1u << Memory.Read8(port + MsgPortOffsets.SignalBit));
                break;
            case 1:
                throw new NotSupportedException("A message port with PA_SOFTINT is not supported yet.");
        }
    }

    /// <summary>Records the return address that the translated code popped for RTS, RTR or RTE.</summary>
    public void ReturnTo(uint address)
    {
        LastReturnAddress = address;
    }

    /// <summary>
    /// Runs the code at the address in the interpreter until it returns from the current frame. The return address of
    /// the frame is at the top of the stack now. A call from the interpreted code to a native or a translated function
    /// runs that function.
    /// </summary>
    /// <summary>The number of instructions that the interpreter ran, because they have no translation.</summary>
    public long InterpretedInstructions { get; private set; }

    /// <summary>For each address where the code went into the interpreter, the number of times.</summary>
    public Dictionary<uint, long> InterpreterEntries { get; } = new();

    private void RunInterpreted(uint address)
    {
        InterpreterEntries[address] = InterpreterEntries.GetValueOrDefault(address) + 1;
        var frame = Cpu.Sp;
        Cpu.Pc = address;
        while (true)
        {
            var pc = Cpu.Pc & Memory.AddressMask;
            if (Libraries.TryGetStub(pc, out var native))
            {
                RunNative(pc, native);
                Cpu.Pc = Cpu.Pop32();
            }
            else if (TryGetFunction(pc, out var function))
            {
                function();
                Cpu.Pc = LastReturnAddress;
            }
            else
            {
                Poll();
                var opcode = Memory.Read16(pc);
                Interpreter.Step();
                InterpretedInstructions++;
                // RTS, RTE and RTR are the only instructions that can return from the frame.
                if (opcode is not (0x4E75 or 0x4E73 or 0x4E77))
                    continue;
            }

            if (Cpu.Sp > frame)
            {
                LastReturnAddress = Cpu.Pc;
                return;
            }
        }
    }

    private void RunNative(uint stub, Action native)
    {
        // Each library call is a safe point for interrupts.
        PollNow();
        if (TraceLibraryCalls)
        {
            var d = Cpu.D;
            var a = Cpu.A;
            Log.WriteLine($"{Libraries.StubName(stub)} from ${Memory.Read32(Cpu.Sp):X6}: "
                          + $"D0=${d[0]:X} D1=${d[1]:X} D2=${d[2]:X} D3=${d[3]:X} A0=${a[0]:X} A1=${a[1]:X}");
        }

        native();
    }

    /// <summary>
    /// Makes a <c>struct Process</c> with a message port and a stack. The process has no input or output streams. The
    /// process does not run until the scheduler starts it. The main process is the task that runs the program.
    /// </summary>
    public uint CreateProcess(string name, sbyte priority, uint stackSize)
    {
        var process = AllocateSystem(ProcessOffsets.Size);
        var nameBytes = Encoding.Latin1.GetBytes(name + "\0");
        var namePointer = AllocateSystem(nameBytes);
        Memory.Write8(process + NodeOffsets.Type, NodeType.Process);
        Memory.Write8(process + NodeOffsets.Priority, (byte)priority);
        Memory.Write32(process + NodeOffsets.Name, namePointer);
        Memory.Write8(process + TaskOffsets.State, TaskOffsets.StateRunning);
        // Signals 0 to 15 belong to the system.
        Memory.Write32(process + TaskOffsets.SignalsAllocated, 0x0000_FFFF);
        ExecList.Initialize(Memory, process + TaskOffsets.MemEntry);

        stackSize = (Math.Max(stackSize, 4096) + 3) & ~3u;
        var stackLower = AllocateSystem(stackSize);
        _processMemory[process] = new ProcessMemory(namePointer, (uint)nameBytes.Length, stackLower, stackSize);
        var stackUpper = stackLower + stackSize;
        Memory.Write32(process + TaskOffsets.StackLower, stackLower);
        Memory.Write32(process + TaskOffsets.StackUpper, stackUpper);
        Memory.Write32(process + ProcessOffsets.StackSize, stackSize);
        Memory.Write32(process + ProcessOffsets.StackBase, stackUpper >> 2);

        // pr_MsgPort signals the process with SIGB_DOS (bit 8).
        var port = process + ProcessOffsets.MsgPort;
        Memory.Write8(port + NodeOffsets.Type, NodeType.MsgPort);
        Memory.Write8(port + MsgPortOffsets.Flags, MsgPortOffsets.PaSignal);
        Memory.Write8(port + MsgPortOffsets.SignalBit, 8);
        Memory.Write32(port + MsgPortOffsets.SignalTask, process);
        ExecList.Initialize(Memory, port + MsgPortOffsets.MessageList, NodeType.Message);

        Memory.Write32(process + ProcessOffsets.TaskNumber, 1);
        return process;
    }

    /// <summary>
    /// Frees the memory that <see cref="CreateProcess"/> allocated for a process: the structure, the name, and the
    /// stack. Exec does this when a task ends. The streams of the process stay open, because they belong to the parent.
    /// </summary>
    public void DeleteProcess(uint process)
    {
        if (!_processMemory.Remove(process, out var memory))
            return;
        FreeSystem(memory.Stack, memory.StackSize);
        FreeSystem(memory.Name, memory.NameSize);
        FreeSystem(process, ProcessOffsets.Size);
    }

    // The sizes come from the allocation, because the program can change the fields of the process.
    private readonly record struct ProcessMemory(uint Name, uint NameSize, uint Stack, uint StackSize);
}
