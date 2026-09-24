using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Translator;

/// <summary>Translates the target program and compiles the result. Run scripts/build-target.sh first.</summary>
public class TargetTranslationTests
{
    [Fact]
    public void Target_TranslatesAndCompiles()
    {
        var directory = Path.Combine(TestPaths.RepositoryRoot, "build", "target");
        if (!File.Exists(Path.Combine(directory, "ESQ.lst")))
            Assert.Skip("The target is not built. Run scripts/build-target.sh.");

        var listing = VasmListing.Read(Path.Combine(directory, "ESQ.lst"));
        var analysis = ProgramAnalysis.Analyze(HunkFile.Read(Path.Combine(directory, "ESQ")), listing);
        var source = CSharpProgramWriter.Write(analysis, "Target", "Esq", "ESQ");

        Assert.Empty(analysis.Warnings);
        Assert.Equal(listing.Lines.Where(line => line.Section == 0 && line.IsInstruction).Sum(line => line.Bytes.Length),
            analysis.Instructions.Values.Sum(code => code.Instruction.Length));
        Assert.NotNull(TestCompiler.Compile(source, "Esq").GetType("Target.Esq"));
    }
}
