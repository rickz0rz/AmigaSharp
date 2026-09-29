using System.Reflection;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Translator;

/// <summary>
/// Tests with samples/ControlFlow: a jump table into local labels that the interpreter runs, interpreted code that
/// calls a translated function, a stack frame, a data hunk, and an exit through a saved stack pointer.
/// </summary>
public class ControlFlowTests
{
    private const uint ExpectedResult = 1111;

    private static readonly string SampleDirectory = Path.Combine(TestPaths.RepositoryRoot, "samples", "ControlFlow");
    private static readonly byte[] Executable = File.ReadAllBytes(Path.Combine(SampleDirectory, "controlflow"));

    private static readonly Lazy<Type> TranslatedType = new(() =>
    {
        var listing = VasmListing.Read(Path.Combine(SampleDirectory, "controlflow.lst"));
        var analysis = ProgramAnalysis.Analyze(HunkFile.Parse(Executable), listing);
        var source = CSharpProgramWriter.Write(analysis, "Samples", "ControlFlow", "controlflow");
        return TestCompiler.Compile(source, "ControlFlow").GetType("Samples.ControlFlow")!;
    });

    [Fact]
    public void TranslatedProgram_ReturnsTheSum()
    {
        var program = (TranslatedProgram)Activator.CreateInstance(TranslatedType.Value, new Core())!;

        Assert.Equal(ExpectedResult, program.Run(Executable));
    }

    [Fact]
    public void InterpretedProgram_ReturnsTheSum()
    {
        Assert.Equal(ExpectedResult, new InterpretedProgram(new Core()).Run(Executable));
    }

    [Fact]
    public void TranslatedProgram_RestoresTheStackPointer()
    {
        var core = new Core();
        var stackPointer = core.Cpu.Sp;
        var program = (TranslatedProgram)Activator.CreateInstance(TranslatedType.Value, core)!;

        program.Run(Executable);

        Assert.Equal(stackPointer, core.Cpu.Sp);
    }

    [Fact]
    public void Analysis_MakesFunctionsOnlyAtCallTargetsAndLabels()
    {
        var listing = VasmListing.Read(Path.Combine(SampleDirectory, "controlflow.lst"));

        var analysis = ProgramAnalysis.Analyze(HunkFile.Parse(Executable), listing);

        Assert.Equal(["Start", "Dispatch", "AddTen", "Frame", "Escape", "deeper"],
            analysis.Functions.Values.Select(function => function.Name));
        Assert.Empty(analysis.Warnings);
    }

    [Fact]
    public void GeneratedCode_UsesTheDispatcherForTheJumpTable()
    {
        var listing = VasmListing.Read(Path.Combine(SampleDirectory, "controlflow.lst"));
        var analysis = ProgramAnalysis.Analyze(HunkFile.Parse(Executable), listing);

        var source = CSharpProgramWriter.Write(analysis, "Samples", "ControlFlow", "controlflow");

        Assert.Contains("var target = 0x20002Eu + (uint)(short)cpu.D[1];", source);
        Assert.Contains("{ core.Dispatch(target); return; }", source);
        Assert.Contains("core.Call(0x200014u, Dispatch);", source);
        // The DBRA loop is a safe point for interrupts.
        Assert.Contains("if (c != 0xFFFFu) { core.Poll(); goto L_200012; }", source);
    }
}
