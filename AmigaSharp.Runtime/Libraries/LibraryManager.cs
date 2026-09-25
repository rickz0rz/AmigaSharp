using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using AmigaSharp.Runtime.Exec;

namespace AmigaSharp.Runtime.Libraries;

/// <summary>
/// Makes the HLE libraries. Each library has a real library base in memory. Below the base is a jump table: each
/// vector is <c>JMP stub</c>. A stub is an address in the ROM area that the dispatcher connects to a C# method.
/// So a program can call a library function, read the jump table, or change a vector with SetFunction.
/// </summary>
public sealed class LibraryManager(Core core)
{
    /// <summary>The stubs start here, in the area of the Kickstart ROM. No 68000 code is at a stub.</summary>
    public const uint StubBase = 0xF8_0000;

    private const int VectorSize = 6;
    private const ushort JmpAbsoluteLong = 0x4EF9;
    private const ushort IllegalOpcode = 0x4AFC;

    private readonly Dictionary<string, Func<Core, AbstractLibrary>> _factories = new();
    private readonly Dictionary<string, AbstractLibrary> _byName = new();
    private readonly Dictionary<uint, AbstractLibrary> _byBase = new();
    private readonly Dictionary<uint, Action> _stubs = new();
    private readonly Dictionary<uint, string> _stubNames = new();
    private readonly Dictionary<string, Func<Core, uint>> _resourceFactories = new();
    private readonly Dictionary<string, uint> _resources = new();
    private uint _nextStub = StubBase;
    private uint _execBase;

    public IEnumerable<AbstractLibrary> Loaded => _byBase.Values;

    public void Register(string name, Func<Core, AbstractLibrary> factory)
    {
        _factories[name] = factory;
    }

    /// <summary>Registers a resource. The factory makes the resource and returns its base.</summary>
    public void RegisterResource(string name, Func<Core, uint> factory)
    {
        _resourceFactories[name] = factory;
    }

    /// <summary>OpenResource: returns the base of the resource, or 0 if the runtime does not have it.</summary>
    public uint OpenResource(string name)
    {
        if (_resources.TryGetValue(name, out var existing))
            return existing;
        if (!_resourceFactories.TryGetValue(name, out var factory))
        {
            core.Log.WriteLine($"OpenResource: the runtime has no {name}.");
            return 0;
        }

        return _resources[name] = factory(core);
    }

    /// <summary>The native function of a stub, if the address is a stub.</summary>
    public bool TryGetStub(uint address, out Action function) => _stubs.TryGetValue(address, out function!);

    /// <summary>The name of the function of a stub, for example "exec.library OpenLibrary (-552)".</summary>
    public string StubName(uint address) => _stubNames.GetValueOrDefault(address, $"${address:X6}");

    public AbstractLibrary? FindByBase(uint libraryBase) => _byBase.GetValueOrDefault(libraryBase);

    /// <summary>
    /// Opens a library by name. Returns null if the library does not exist or if its version is less than the minimum.
    /// </summary>
    public AbstractLibrary? Open(string name, uint minimumVersion)
    {
        // A program can give a path, for example "libs:diskfont.library".
        var fileName = name[(name.LastIndexOfAny([':', '/']) + 1)..];
        if (!_byName.TryGetValue(fileName, out var library))
        {
            if (!_factories.TryGetValue(fileName, out var factory))
            {
                core.Log.WriteLine($"OpenLibrary: the runtime has no {name}.");
                return null;
            }

            library = factory(core);
            Place(library);
            _byName.Add(fileName, library);
            _byBase.Add(library.Base, library);
        }

        if (library.Version < minimumVersion)
            return null;

        library.OpenCount++;
        core.Memory.Write16(library.Base + LibraryOffsets.OpenCount, library.OpenCount);
        return library;
    }

    /// <summary>
    /// Closes a library. The library stays in memory, because exec expunges libraries only when memory is low.
    /// </summary>
    public void Close(uint libraryBase)
    {
        if (!_byBase.TryGetValue(libraryBase, out var library))
            throw new InvalidOperationException($"CloseLibrary: no library at ${libraryBase:X6}.");
        if (library.OpenCount == 0)
            throw new InvalidOperationException($"CloseLibrary: {library.Name} is not open.");

        library.OpenCount--;
        core.Memory.Write16(library.Base + LibraryOffsets.OpenCount, library.OpenCount);
    }

    /// <summary>SetFunction: changes the target of a vector. Returns the old target.</summary>
    public uint SetFunction(uint libraryBase, short offset, uint newFunction)
    {
        var vector = libraryBase + (uint)offset;
        var memory = core.Memory;
        if (memory.Read16(vector) != JmpAbsoluteLong)
            throw new InvalidOperationException($"SetFunction: the vector at ${vector:X6} is not a JMP instruction.");

        var old = memory.Read32(vector + 2);
        memory.Write32(vector + 2, newFunction);
        return old;
    }

    /// <summary>Adds a stub for a native function. Returns the address of the stub.</summary>
    public uint AddStub(Action function, string name)
    {
        var address = _nextStub;
        _nextStub += 2;
        core.Memory.Write16(address, IllegalOpcode);
        _stubs.Add(address, function);
        _stubNames.Add(address, name);
        return address;
    }

    private void Place(AbstractLibrary library)
    {
        var memory = core.Memory;
        var functions = library.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<LibraryFunctionOffsetAttribute>()))
            .Where(function => function.Attribute != null)
            .ToDictionary(function => (int)function.Attribute!.Offset, function => function.Method);

        var lowestOffset = Math.Min(library.LowestOffset, functions.Keys.DefaultIfEmpty(-6).Min());
        var negativeSize = (uint)((-lowestOffset + VectorSize + 3) & ~3);
        var start = core.AllocateSystem(negativeSize + library.PositiveSize);
        library.Base = start + negativeSize;

        for (var offset = -6; offset >= lowestOffset; offset -= VectorSize)
        {
            var function = functions.TryGetValue(offset, out var method)
                ? CreateCall(library, method)
                : MissingFunction(library.Name, offset);
            var vector = library.Base + (uint)offset;
            var name = $"{library.Name} {method?.Name ?? "?"} ({offset})";
            memory.Write16(vector, JmpAbsoluteLong);
            memory.Write32(vector + 2, AddStub(function, name));
        }

        var libraryName = core.AllocateSystem(Encoding.Latin1.GetBytes(library.Name + "\0"));
        var idString = core.AllocateSystem(Encoding.Latin1.GetBytes(library.IdString + "\r\n\0"));
        memory.Write8(library.Base + NodeOffsets.Type, library is AbstractDevice ? NodeType.Device : NodeType.Library);
        memory.Write32(library.Base + NodeOffsets.Name, libraryName);
        memory.Write16(library.Base + LibraryOffsets.NegativeSize, (ushort)negativeSize);
        memory.Write16(library.Base + LibraryOffsets.PositiveSize, library.PositiveSize);
        memory.Write16(library.Base + LibraryOffsets.Version, library.Version);
        memory.Write16(library.Base + LibraryOffsets.Revision, library.Revision);
        memory.Write32(library.Base + LibraryOffsets.IdString, idString);
        library.Initialize();

        // Exec is the first library. Each library goes in the library list of ExecBase, and each device in the
        // device list.
        if (_execBase == 0)
            _execBase = library.Base;
        var list = library is AbstractDevice ? ExecBaseOffsets.DeviceList : ExecBaseOffsets.LibraryList;
        ExecList.AddTail(memory, _execBase + list, library.Base);
    }

    private static Action MissingFunction(string library, int offset) =>
        () => throw new MissingLibraryFunctionException(library, offset);

    /// <summary>
    /// Makes a delegate that reads the parameters from their registers, calls the method, and puts the result in D0.
    /// </summary>
    private Action CreateCall(AbstractLibrary library, MethodInfo method)
    {
        var cpu = Expression.Constant(core.Cpu);
        var dataRegisters = Expression.Field(cpu, nameof(Cpu.CpuState.D));
        var addressRegisters = Expression.Field(cpu, nameof(Cpu.CpuState.A));

        var arguments = method.GetParameters().Select<ParameterInfo, Expression>(parameter =>
        {
            var register = parameter.GetCustomAttribute<RegisterAttribute>()
                           ?? throw new InvalidOperationException(
                               $"{library.Name} {method.Name}: the parameter {parameter.Name} has no register attribute.");
            Expression value = Expression.ArrayIndex(
                register.IsAddressRegister ? addressRegisters : dataRegisters,
                Expression.Constant(register.Number));
            return parameter.ParameterType == typeof(bool)
                ? Expression.NotEqual(value, Expression.Constant(0u))
                : Expression.Convert(value, parameter.ParameterType);
        });

        Expression call = Expression.Call(Expression.Constant(library), method, arguments);
        if (method.ReturnType == typeof(uint) || method.ReturnType == typeof(int))
        {
            call = Expression.Assign(
                Expression.ArrayAccess(dataRegisters, Expression.Constant(0)),
                Expression.Convert(call, typeof(uint)));
        }
        else if (method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException($"{library.Name} {method.Name}: the return type must be void, uint or int.");
        }

        return Expression.Lambda<Action>(call).Compile();
    }
}
