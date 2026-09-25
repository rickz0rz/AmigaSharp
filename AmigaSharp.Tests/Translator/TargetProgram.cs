using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Translator;

/// <summary>The target program: the executable and the listing that scripts/build-target.sh makes, translated once.</summary>
public static class TargetProgram
{
    public static readonly string BuildDirectory = Path.Combine(TestPaths.RepositoryRoot, "build", "target");

    /// <summary>The drive of the Prevue machine, which is not in the repository.</summary>
    public static readonly string DriveDirectory = Path.Combine(TestPaths.RepositoryRoot, "target-source", "binaries");

    public static string ExecutablePath => Path.Combine(BuildDirectory, "ESQ");
    public static string ListingPath => Path.Combine(BuildDirectory, "ESQ.lst");

    public static bool IsBuilt => File.Exists(ExecutablePath) && File.Exists(ListingPath);

    private static readonly Lazy<(ProgramAnalysis Analysis, string Source)> Translation = new(() =>
    {
        var listing = VasmListing.Read(ListingPath);
        var analysis = ProgramAnalysis.Analyze(HunkFile.Read(ExecutablePath), listing);
        return (analysis, CSharpProgramWriter.Write(analysis, "Target", "Esq", "ESQ"));
    });

    private static readonly Lazy<Type> CompiledType = new(() =>
        TestCompiler.Compile(Translation.Value.Source, "Esq").GetType("Target.Esq")!);

    public static ProgramAnalysis Analysis => Translation.Value.Analysis;

    public static Type Type => CompiledType.Value;
}
