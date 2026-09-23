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

    // The exception vector table uses $000 to $3FF. Allocations start above it.
    // TODO: Replace this bump allocator with exec memory lists (AllocMem and FreeMem).
    private const uint FirstFreeAddress = 0x1000;
    private const uint LastFreeAddress = 0x20_0000;
    private const uint StackSize = 8 * 1024;

    // Each library vector is a 6-byte JMP instruction.
    private const int VectorSize = 6;

    private readonly Dictionary<string, Func<Core, AbstractLibrary>> _libraryFactories = new();
    private readonly Dictionary<string, AbstractLibrary> _librariesByName = new();
    private readonly Dictionary<uint, AbstractLibrary> _librariesByBase = new();
    private readonly Dictionary<uint, Action> _vectors = new();
    private uint _nextFreeAddress = FirstFreeAddress;

    public Memory Memory { get; } = new();
    public CpuState Cpu { get; }

    /// <summary>The stream that the default output file handle writes to.</summary>
    public Stream Output { get; }

    public Core(Stream? output = null)
    {
        Cpu = new CpuState(Memory);
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
