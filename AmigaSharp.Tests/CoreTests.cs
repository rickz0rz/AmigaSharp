using System.Text;
using AmigaSharp.Runtime;
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
        _core = new Core(_output);
    }

    [Fact]
    public void SysBase_PointsToExecLibraryStructure()
    {
        var sysBase = _core.Memory.Read32(Core.SysBaseAddress);

        Assert.Equal("exec.library", _core.Memory.ReadCString(_core.Memory.Read32(sysBase + LibraryOffsets.NodeName)));
        Assert.Equal(40, _core.Memory.Read16(sysBase + LibraryOffsets.Version));
    }

    [Fact]
    public void StackPointer_IsInsideAllocatedMemory()
    {
        Assert.InRange(_core.Cpu.Sp, 0x1000u, 0x20_0000u);
        Assert.Equal(0u, _core.Cpu.Sp & 3);
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

        Assert.Throws<InvalidOperationException>(() => _core.CallVector(sysBase, -6));
    }

    private uint CallOpenLibrary(string name, uint version)
    {
        _core.Cpu.A[1] = _core.AllocateStatic(Encoding.Latin1.GetBytes(name + "\0"));
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
