using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Cpu;

namespace AmigaSharp.Tests.Cpu;

/// <summary>
/// Tests of the behaviour that the SingleStepTests vectors do not cover. All the vectors start in supervisor mode
/// with the trace flag clear.
/// </summary>
public class InterpreterTests
{
    private const uint CodeAddress = 0x1000;
    private const uint Handler = 0x3000;
    private const uint UserStack = 0x6000;
    private const uint SupervisorStack = 0x8000;

    private readonly Memory _memory = new();
    private readonly CpuState _cpu;
    private readonly Interpreter _interpreter;

    public InterpreterTests()
    {
        _cpu = new CpuState(_memory);
        _interpreter = new Interpreter(_cpu);
        _cpu.Sr = 0x2700;
        _cpu.Ssp = SupervisorStack;
        _cpu.Usp = UserStack;
        _cpu.Pc = CodeAddress;
        for (var vector = 2; vector < 48; vector++)
            _memory.Write32((uint)vector * 4, Handler + (uint)vector * 0x10);
    }

    [Theory]
    [InlineData(new ushort[] { 0x46FC, 0x2700 })] // MOVE #$2700,SR
    [InlineData(new ushort[] { 0x027C, 0xF8FF })] // ANDI #$F8FF,SR
    [InlineData(new ushort[] { 0x4E73 })] // RTE
    [InlineData(new ushort[] { 0x4E72, 0x2000 })] // STOP #$2000
    [InlineData(new ushort[] { 0x4E70 })] // RESET
    [InlineData(new ushort[] { 0x4E60 })] // MOVE A0,USP
    public void PrivilegedInstruction_InUserMode_RaisesPrivilegeViolation(ushort[] code)
    {
        EnterUserMode();
        Code(code);

        _interpreter.Step();

        AssertException(ExceptionVector.PrivilegeViolation, stackedPc: CodeAddress, stackedSr: 0x0000);
        Assert.Equal(UserStack, _cpu.Usp);
    }

    [Fact]
    public void RuntimeInterpreter_TakesTheException_WhenTheProgramInstalledAHandler()
    {
        var interpreter = new Interpreter(_cpu) { ExceptionsAreFatal = true };
        EnterUserMode();
        Code(0x46FC, 0x2700); // MOVE #$2700,SR

        interpreter.Step();

        AssertException(ExceptionVector.PrivilegeViolation, stackedPc: CodeAddress, stackedSr: 0x0000);
    }

    [Fact]
    public void RuntimeInterpreter_Throws_WhenTheVectorIsZero()
    {
        var interpreter = new Interpreter(_cpu) { ExceptionsAreFatal = true };
        _memory.Write32(ExceptionVector.PrivilegeViolation * 4, 0);
        EnterUserMode();
        Code(0x46FC, 0x2700);

        var trap = Assert.Throws<CpuTrapException>(interpreter.Step);

        Assert.Equal(ExceptionVector.PrivilegeViolation, trap.Vector);
        Assert.Equal(CodeAddress, _cpu.Pc);
    }

    [Fact]
    public void MoveFromSr_InUserMode_IsAllowed()
    {
        EnterUserMode();
        _cpu.Ccr = 0x1F;
        Code(0x40C0); // MOVE SR,D0

        _interpreter.Step();

        Assert.Equal(0x001Fu, _cpu.D[0]);
        Assert.Equal(CodeAddress + 2, _cpu.Pc);
    }

    [Fact]
    public void Rte_ToUserMode_SwitchesStackPointer()
    {
        _cpu.Push32(0x2000);
        _cpu.Push16(0x0015);
        Code(0x4E73); // RTE

        _interpreter.Step();

        Assert.False(_cpu.S);
        Assert.Equal(0x0015, _cpu.Sr);
        Assert.Equal(0x2000u, _cpu.Pc);
        Assert.Equal(UserStack, _cpu.A[7]);
        Assert.Equal(SupervisorStack, _cpu.Ssp);
    }

    [Fact]
    public void MoveUsp_CopiesBothWays()
    {
        _cpu.A[0] = 0x1234_5678;
        Code(0x4E60, 0x4E69); // MOVE A0,USP / MOVE USP,A1

        _interpreter.Step();
        _interpreter.Step();

        Assert.Equal(0x1234_5678u, _cpu.Usp);
        Assert.Equal(0x1234_5678u, _cpu.A[1]);
        Assert.Equal(SupervisorStack, _cpu.A[7]);
    }

    [Fact]
    public void UserModeException_UsesSupervisorStack()
    {
        EnterUserMode();
        Code(0x4E43); // TRAP #3

        _interpreter.Step();

        AssertException(ExceptionVector.Trap0 + 3, stackedPc: CodeAddress + 2, stackedSr: 0x0000);
        Assert.Equal(UserStack, _cpu.Usp);
    }

    [Theory]
    [InlineData((ushort)0x4AFC, ExceptionVector.IllegalInstruction)] // ILLEGAL
    [InlineData((ushort)0xA123, ExceptionVector.LineA)]
    [InlineData((ushort)0xF000, ExceptionVector.LineF)]
    [InlineData((ushort)0x1008, ExceptionVector.IllegalInstruction)] // MOVE.B A0,D0 does not exist.
    public void UnimplementedOpcode_StacksItsOwnAddress(ushort opcode, int vector)
    {
        Code(opcode);

        _interpreter.Step();

        AssertException(vector, stackedPc: CodeAddress, stackedSr: 0x2700);
    }

    [Fact]
    public void Trace_AfterInstruction_RaisesTraceException()
    {
        _cpu.T = true;
        Code(0x4E71); // NOP

        _interpreter.Step();

        AssertException(ExceptionVector.Trace, stackedPc: CodeAddress + 2, stackedSr: 0xA700);
        Assert.False(_cpu.T);
    }

    [Fact]
    public void Trace_AfterTrap_TracesTheTrapHandler()
    {
        _cpu.T = true;
        Code(0x4E40); // TRAP #0

        _interpreter.Step();

        // The trace frame is on top. It stacks the address of the TRAP handler.
        Assert.Equal(TraceHandler(), _cpu.Pc);
        Assert.Equal(SupervisorStack - 12, _cpu.Sp);
        Assert.Equal(VectorHandler(ExceptionVector.Trap0), _memory.Read32(_cpu.Sp + 2));
        Assert.Equal(CodeAddress + 2, _memory.Read32(_cpu.Sp + 8));
    }

    [Fact]
    public void Trace_AfterIllegalInstruction_DoesNotTrace()
    {
        _cpu.T = true;
        Code(0x4AFC); // ILLEGAL

        _interpreter.Step();

        AssertException(ExceptionVector.IllegalInstruction, stackedPc: CodeAddress, stackedSr: 0xA700);
    }

    [Fact]
    public void Stop_LoadsSrAndWaits()
    {
        Code(0x4E72, 0x2015, 0x4E71); // STOP #$2015 / NOP

        _interpreter.Step();
        _interpreter.Step();

        Assert.True(_cpu.Stopped);
        Assert.Equal(0x2015, _cpu.Sr);
        Assert.Equal(CodeAddress + 4, _cpu.Pc);
    }

    [Fact]
    public void BranchToOddAddress_ThrowsAddressError()
    {
        Code(0x6001); // BRA.S *+3

        Assert.Throws<AddressErrorException>(() => _interpreter.Step());
    }

    [Fact]
    public void BytePushToA7_KeepsStackAligned()
    {
        _cpu.D[0] = 0xAB;
        Code(0x1F00); // MOVE.B D0,-(A7)

        _interpreter.Step();

        Assert.Equal(SupervisorStack - 2, _cpu.Sp);
        Assert.Equal(0xAB, _memory.Read8(SupervisorStack - 2));
    }

    [Fact]
    public void Decoder_FormatsMotorolaSyntax()
    {
        Code(0x48E7, 0x3F3E, 0x4CDF, 0x7CFC, 0x41FA, 0xFFFE, 0x2030, 0x8804);

        Assert.Equal("MOVEM.L D2/D3/D4/D5/D6/D7/A2/A3/A4/A5/A6,-(SP)", Decode(0x1000).ToString());
        Assert.Equal("MOVEM.L (SP)+,D2/D3/D4/D5/D6/D7/A2/A3/A4/A5/A6", Decode(0x1004).ToString());
        Assert.Equal("LEA $1008(PC),A0", Decode(0x1008).ToString());
        Assert.Equal("MOVE.L 4(A0,A0.L),D0", Decode(0x100C).ToString());
    }

    private Instruction Decode(uint address) => Decoder.Decode(address, _memory.Read16);

    private void EnterUserMode()
    {
        _cpu.Sr = 0x0000;
    }

    private void Code(params ushort[] words)
    {
        for (var i = 0; i < words.Length; i++)
            _memory.Write16(CodeAddress + (uint)i * 2, words[i]);
    }

    private static uint VectorHandler(int vector) => Handler + (uint)vector * 0x10;

    private static uint TraceHandler() => VectorHandler(ExceptionVector.Trace);

    private void AssertException(int vector, uint stackedPc, ushort stackedSr)
    {
        Assert.True(_cpu.S);
        Assert.False(_cpu.T);
        Assert.Equal(VectorHandler(vector), _cpu.Pc);
        Assert.Equal(SupervisorStack - 6, _cpu.Sp);
        Assert.Equal(stackedSr, _memory.Read16(_cpu.Sp));
        Assert.Equal(stackedPc, _memory.Read32(_cpu.Sp + 2));
    }
}
