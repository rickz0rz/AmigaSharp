using System.Text;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Libraries;

namespace AmigaSharp.Tests;

public class CoreTests
{
    private const short OpenLibrary = -552;
    private const short CloseLibrary = -414;

    private readonly MemoryStream _output = new();
    private readonly Core _core;

    public CoreTests()
    {
        _core = new Core(_output) { Log = TextWriter.Null };
    }

    [Fact]
    public void SysBase_PointsToExecLibraryStructure()
    {
        var sysBase = _core.Memory.Read32(Core.SysBaseAddress);

        Assert.Equal("exec.library", _core.Memory.ReadCString(_core.Memory.Read32(sysBase + LibraryOffsets.NodeName)));
        Assert.Equal(40, _core.Memory.Read16(sysBase + LibraryOffsets.Version));
    }

    [Fact]
    public void StackPointer_IsAtTheTopOfTheProcessStack()
    {
        var process = _core.MainProcess;

        Assert.Equal(_core.Memory.Read32(process + TaskOffsets.StackUpper), _core.Cpu.Sp);
        Assert.InRange(_core.Cpu.Sp, Core.FastStart, Core.FastEnd);
        Assert.Equal(0u, _core.Cpu.Sp & 3);
    }

    [Fact]
    public void ThisTask_IsTheMainProcess()
    {
        var sysBase = _core.Memory.Read32(Core.SysBaseAddress);

        Assert.Equal(_core.MainProcess, _core.Memory.Read32(sysBase + ExecBaseOffsets.ThisTask));
        Assert.Equal(NodeType.Process, _core.Memory.Read8(_core.MainProcess + NodeOffsets.Type));
    }

    [Fact]
    public void LibraryVector_IsJmpToStub()
    {
        var sysBase = _core.Memory.Read32(Core.SysBaseAddress);

        Assert.Equal(0x4EF9, _core.Memory.Read16(sysBase + unchecked((uint)OpenLibrary)));
        Assert.InRange(_core.Memory.Read32(sysBase + unchecked((uint)OpenLibrary) + 2), LibraryManager.StubBase, LibraryManager.StubBase + 0x1000);
    }

    [Fact]
    public void OpenLibrary_UnknownName_ReturnsZero()
    {
        Assert.Equal(0u, CallOpenLibrary("nosuch.library", 0));
    }

    [Fact]
    public void OpenLibrary_VersionTooHigh_ReturnsZero()
    {
        Assert.Equal(0u, CallOpenLibrary("dos.library", 99));
    }

    [Fact]
    public void OpenLibrary_Twice_ReturnsSameBaseAndCountsOpens()
    {
        var first = CallOpenLibrary("dos.library", 36);
        var second = CallOpenLibrary("dos.library", 36);

        Assert.NotEqual(0u, first);
        Assert.Equal(first, second);
        Assert.Equal(2, _core.Memory.Read16(first + LibraryOffsets.OpenCount));
    }

    [Fact]
    public void CloseLibrary_ThenOpenAnother_DoesNotReuseAddress()
    {
        _core.RegisterLibrary("test.library", _ => new TestLibrary());

        var dos = CallOpenLibrary("dos.library", 0);
        CallCloseLibrary(dos);
        var test = CallOpenLibrary("test.library", 0);

        Assert.NotEqual(0u, test);
        Assert.NotEqual(dos, test);
        Assert.Equal(0, _core.Memory.Read16(dos + LibraryOffsets.OpenCount));
    }

    [Fact]
    public void CallVector_WithNoFunction_Throws()
    {
        var sysBase = _core.Memory.Read32(Core.SysBaseAddress);

        var exception = Assert.Throws<MissingLibraryFunctionException>(() => _core.CallVector(sysBase, -6));
        Assert.Equal(("exec.library", -6), (exception.Library, exception.Offset));
    }

    private uint CallOpenLibrary(string name, uint version)
    {
        _core.Cpu.A[1] = _core.AllocateSystem(Encoding.Latin1.GetBytes(name + "\0"));
        _core.Cpu.D[0] = version;
        _core.CallVector(_core.Memory.Read32(Core.SysBaseAddress), OpenLibrary);
        return _core.Cpu.D[0];
    }

    private void CallCloseLibrary(uint libraryBase)
    {
        _core.Cpu.A[1] = libraryBase;
        _core.CallVector(_core.Memory.Read32(Core.SysBaseAddress), CloseLibrary);
    }

    private sealed class TestLibrary : AbstractLibrary
    {
        public override string Name => "test.library";
        public override ushort Version => 1;

        [LibraryFunctionOffset(-30)]
        public void Nothing()
        {
        }
    }
}
