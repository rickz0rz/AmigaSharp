using System.Text;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Translator;

/// <summary>
/// Tests with samples/FileIO: 68000 code that uses exec.library and dos.library to write, read and delete a file.
/// </summary>
public sealed class FileIOTests : IDisposable
{
    private const string ExpectedOutput = "Read 11 bytes: hello, file\n-12-002a-abc|de   |\nIoErr 205\n";

    private static readonly string SampleDirectory = Path.Combine(TestPaths.RepositoryRoot, "samples", "FileIO");
    private static readonly byte[] Executable = File.ReadAllBytes(Path.Combine(SampleDirectory, "fileio"));

    private static readonly Lazy<Type> TranslatedType = new(() =>
    {
        var listing = VasmListing.Read(Path.Combine(SampleDirectory, "fileio.lst"));
        var analysis = ProgramAnalysis.Analyze(HunkFile.Parse(Executable), listing);
        var source = CSharpProgramWriter.Write(analysis, "Samples", "FileIO", "fileio");
        return TestCompiler.Compile(source, "FileIO").GetType("Samples.FileIO")!;
    });

    private readonly string _root = Directory.CreateTempSubdirectory("AmigaSharp-fileio-").FullName;

    [Fact]
    public void TranslatedProgram_WritesAndReadsTheFile()
    {
        var output = new MemoryStream();
        var core = new Core(output, new MemoryStream(), _root);
        var program = (TranslatedProgram)Activator.CreateInstance(TranslatedType.Value, core)!;

        Assert.Equal(0u, program.Run(Executable));
        Assert.Equal(ExpectedOutput, Encoding.Latin1.GetString(output.ToArray()));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void InterpretedProgram_WritesAndReadsTheFile()
    {
        var output = new MemoryStream();
        var core = new Core(output, new MemoryStream(), _root);

        Assert.Equal(0u, new InterpretedProgram(core).Run(Executable));
        Assert.Equal(ExpectedOutput, Encoding.Latin1.GetString(output.ToArray()));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
