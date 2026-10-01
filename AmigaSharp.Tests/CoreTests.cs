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

    [Fact]
    public void Call_ContinuesAfterAnRtsThatIsAJump()
    {
        // The code at "jump" removes a saved long from the stack, and jumps to "answer" with MOVE.L #answer,-(SP) and
        // RTS, as a decruncher does. The RTS leaves the return address of the call on the stack.
        var answer = _core.AllocateSystem([0x70, 0x2A, 0x4E, 0x75]); // MOVEQ #42,D0; RTS
        var jump = _core.AllocateSystem([0x58, 0x8F, 0x2F, 0x3C, .. BigEndian(answer), 0x4E, 0x75]); // ADDQ.L #4,SP
        var start = _core.AllocateSystem([0x4E, 0x71]);
        // A translated function saves a long, and jumps to the code.
        _core.RegisterFunction(start, () =>
        {
            _core.Cpu.Push32(0x1234_5678);
            _core.Dispatch(jump);
        });
        var stackPointer = _core.Cpu.Sp;

        _core.CallAddress(Core.ExitAddress, start);

        Assert.Equal(42u, _core.Cpu.D[0]);
        Assert.Equal(stackPointer, _core.Cpu.Sp);
    }

    [Fact]
    public void Dispatch_InterpretsNewCodeOverATranslatedFunction()
    {
        var code = _core.AllocateSystem([0x70, 0x01, 0x4E, 0x75, 0x4E, 0x71, 0x4E, 0x71]); // MOVEQ #1,D0; RTS
        _core.RegisterFunction(code, () =>
        {
            _core.Cpu.D[0] = 7;
            _core.ReturnTo(_core.Cpu.Pop32());
        });

        _core.CallAddress(Core.ExitAddress, code);
        Assert.Equal(7u, _core.Cpu.D[0]);

        // The program writes new code, for example a decruncher. The old translation must not run.
        _core.Memory.WriteBytes(code, [0x70, 0x02, 0x4E, 0x75]); // MOVEQ #2,D0; RTS
        _core.CallAddress(Core.ExitAddress, code);
        Assert.Equal(2u, _core.Cpu.D[0]);
    }

    private static byte[] BigEndian(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

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
