using System.Reflection;
using System.Text;
using AmigaSharp.Runtime.Cpu;
using AmigaSharp.Runtime.Libraries;
using AmigaSharp.Runtime.Libraries.Native;

namespace AmigaSharp.Runtime;

/// <summary>
/// One emulated Amiga: the memory, the CPU state and the HLE libraries.
/// </summary>
public sealed class Core
{
    /// <summary>Address 4 holds the pointer to ExecBase.</summary>
    public const uint SysBaseAddress = 4;

    /// <summary>
    /// The return address of the program. The entry point returns to this address when the program ends.
    /// No code is at this address.
    /// </summary>
    public const uint ExitAddress = 0x00FF_FFF0;

    // The exception vector table uses $000 to $3FF. Allocations start above it and stop below the program hunks.
    // TODO: Replace this bump allocator with exec memory lists (AllocMem and FreeMem).
    private const uint FirstFreeAddress = 0x1000;
    private const uint LastFreeAddress = Loader.HunkLayout.ChipBase;
    private const uint StackSize = 16 * 1024;

    // Each library vector is a 6-byte JMP instruction.
    private const int VectorSize = 6;

    private readonly Dictionary<string, Func<Core, AbstractLibrary>> _libraryFactories = new();
    private readonly Dictionary<string, AbstractLibrary> _librariesByName = new();
    private readonly Dictionary<uint, AbstractLibrary> _librariesByBase = new();
    private readonly Dictionary<uint, Action> _vectors = new();
    private readonly Dictionary<uint, Action> _functions = new();
    private uint _nextFreeAddress = FirstFreeAddress;

    public Memory Memory { get; } = new();
    public CpuState Cpu { get; }

    /// <summary>Runs the code that has no translated function. A 68000 exception in that code is fatal.</summary>
    public Interpreter Interpreter { get; }

    /// <summary>The address that the last RTS, RTR or RTE returned to.</summary>
    public uint LastReturnAddress { get; set; }

    /// <summary>The stream that the default output file handle writes to.</summary>
    public Stream Output { get; }

    public Core(Stream? output = null)
    {
        Cpu = new CpuState(Memory);
        Interpreter = new Interpreter(Cpu) { ExceptionsAreFatal = true };
        Output = output ?? Console.OpenStandardOutput();

        RegisterLibrary("exec.library", core => new ExecLibrary(core));
        RegisterLibrary("dos.library", core => new DosLibrary(core));

        // Exec is always open.
        var exec = OpenLibrary("exec.library", 0)!;
        Memory.Write32(SysBaseAddress, exec.Base);

        var stackBottom = Allocate(StackSize);
        Cpu.Sp = stackBottom + StackSize;
    }

    public void RegisterLibrary(string name, Func<Core, AbstractLibrary> factory)
    {
        _libraryFactories[name] = factory;
    }

    /// <summary>Allocates memory that the program never frees. The address is long-aligned and the memory is zero.</summary>
    public uint Allocate(uint size)
    {
        var address = _nextFreeAddress;
        var next = (address + size + 3) & ~3u;
        if (next > LastFreeAddress)
            throw new OutOfMemoryException($"The allocator has no space for {size} bytes.");
        _nextFreeAddress = next;
        return address;
    }

    public uint AllocateStatic(ReadOnlySpan<byte> bytes)
    {
        var address = Allocate((uint)bytes.Length);
        Memory.WriteBytes(address, bytes);
        return address;
    }

    /// <summary>
    /// Opens a library by name. Returns null if the library does not exist or if its version is less than the minimum.
    /// </summary>
    public AbstractLibrary? OpenLibrary(string name, uint minimumVersion)
    {
        if (!_librariesByName.TryGetValue(name, out var library))
        {
            if (!_libraryFactories.TryGetValue(name, out var factory))
                return null;

            library = factory(this);
            PlaceLibrary(library);
            _librariesByName.Add(name, library);
            _librariesByBase.Add(library.Base, library);
        }

        if (library.Version < minimumVersion)
            return null;

        library.OpenCount++;
        Memory.Write16(library.Base + LibraryOffsets.OpenCount, library.OpenCount);
        return library;
    }

    /// <summary>
    /// Closes a library. The library stays in memory, because exec expunges libraries only when memory is low.
    /// </summary>
    public void CloseLibrary(uint libraryBase)
    {
        if (!_librariesByBase.TryGetValue(libraryBase, out var library))
            throw new InvalidOperationException($"CloseLibrary: no library at ${libraryBase:X6}.");
        if (library.OpenCount == 0)
            throw new InvalidOperationException($"CloseLibrary: {library.Name} is not open.");

        library.OpenCount--;
        Memory.Write16(library.Base + LibraryOffsets.OpenCount, library.OpenCount);
    }

    /// <summary>Calls the library function at the offset from the library base, as <c>JSR offset(A6)</c> does.</summary>
    public void CallVector(uint libraryBase, short offset)
    {
        var address = (libraryBase + (uint)offset) & Memory.AddressMask;
        if (!_vectors.TryGetValue(address, out var function))
            throw new InvalidOperationException(
                $"No library function at ${address:X6} (base ${libraryBase:X6}, offset {offset}).");
        function();
    }

    /// <summary>Makes a translated function the code at the address.</summary>
    public void RegisterFunction(uint address, Action function)
    {
        _functions[address & Memory.AddressMask] = function;
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
        try
        {
            function();
        }
        catch (StackUnwindException unwind) when (unwind.ReturnAddress == returnAddress)
        {
            return;
        }

        if (LastReturnAddress != returnAddress)
            throw new StackUnwindException(LastReturnAddress);
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
        target &= Memory.AddressMask;
        if (_vectors.TryGetValue(target, out var native))
        {
            native();
            LastReturnAddress = Cpu.Pop32();
            return;
        }

        if (_functions.TryGetValue(target, out var function))
        {
            function();
            return;
        }

        // The vectors of a library are below its base. An address there with no native function is a library
        // function that the runtime does not implement.
        foreach (var library in _librariesByBase.Values)
        {
            var negativeSize = Memory.Read16(library.Base + LibraryOffsets.NegativeSize);
            if (target < library.Base && target >= library.Base - negativeSize)
                throw new MissingLibraryFunctionException(library.Name, (int)target - (int)library.Base);
        }

        RunInterpreted(target);
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
    private void RunInterpreted(uint address)
    {
        var frame = Cpu.Sp;
        Cpu.Pc = address;
        while (true)
        {
            var pc = Cpu.Pc & Memory.AddressMask;
            if (_vectors.TryGetValue(pc, out var native))
            {
                native();
                Cpu.Pc = Cpu.Pop32();
            }
            else if (_functions.TryGetValue(pc, out var function))
            {
                function();
                Cpu.Pc = LastReturnAddress;
            }
            else
            {
                var opcode = Memory.Read16(pc);
                Interpreter.Step();
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

    private void PlaceLibrary(AbstractLibrary library)
    {
        var functions = library.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => (method, attribute: method.GetCustomAttribute<LibraryFunctionOffsetAttribute>()))
            .Where(function => function.attribute != null)
            .ToList();

        // The vectors are below the base and the library structure is above it.
        var negativeSize = functions.Count == 0 ? 0 : functions.Max(function => -function.attribute!.Offset) + VectorSize;
        negativeSize = (negativeSize + 3) & ~3;
        var start = Allocate((uint)(negativeSize + library.PositiveSize));
        library.Base = start + (uint)negativeSize;

        var name = AllocateStatic(Encoding.Latin1.GetBytes(library.Name + "\0"));
        Memory.Write8(library.Base + LibraryOffsets.NodeType, LibraryOffsets.NodeTypeLibrary);
        Memory.Write32(library.Base + LibraryOffsets.NodeName, name);
        Memory.Write16(library.Base + LibraryOffsets.NegativeSize, (ushort)negativeSize);
        Memory.Write16(library.Base + LibraryOffsets.PositiveSize, library.PositiveSize);
        Memory.Write16(library.Base + LibraryOffsets.Version, library.Version);
        Memory.Write16(library.Base + LibraryOffsets.Revision, library.Revision);

        foreach (var (method, attribute) in functions)
        {
            var address = (library.Base + (uint)attribute!.Offset) & Memory.AddressMask;
            _vectors.Add(address, method.CreateDelegate<Action>(library));
        }
    }
}
