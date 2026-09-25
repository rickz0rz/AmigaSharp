namespace AmigaSharp.Tests.Translator;

/// <summary>Translates the target program and compiles the result. Run scripts/build-target.sh first.</summary>
public class TargetTranslationTests
{
    [Fact]
    public void Target_TranslatesAndCompiles()
    {
        if (!TargetProgram.IsBuilt)
            Assert.Skip("The target is not built. Run scripts/build-target.sh.");

        var listing = AmigaSharp.Translator.VasmListing.Read(TargetProgram.ListingPath);
        var analysis = TargetProgram.Analysis;

        Assert.Empty(analysis.Warnings);
        Assert.Equal(listing.Lines.Where(line => line.Section == 0 && line.IsInstruction).Sum(line => line.Bytes.Length),
            analysis.Instructions.Values.Sum(code => code.Instruction.Length));
        Assert.NotNull(TargetProgram.Type);
    }
}
