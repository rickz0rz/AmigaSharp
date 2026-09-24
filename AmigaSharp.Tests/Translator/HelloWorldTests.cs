using System.Text;
using AmigaSharp.Generated;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Translator;

/// <summary>
/// Tests with samples/HelloWorld: hello.s, the executable that vasm made from it, and the listing.
/// </summary>
public class HelloWorldTests
{
    private static readonly string SampleDirectory = Path.Combine(TestPaths.RepositoryRoot, "samples", "HelloWorld");
    private static readonly byte[] Executable = File.ReadAllBytes(Path.Combine(SampleDirectory, "hello"));

    [Fact]
    public void TranslatedProgram_PrintsHelloWorld_AndReturnsZero()
    {
        var output = new MemoryStream();

        var result = new HelloWorld(new Core(output)).Run(Executable);

        Assert.Equal(0u, result);
        Assert.Equal("Hello World!\n", Encoding.Latin1.GetString(output.ToArray()));
    }

    [Fact]
    public void InterpretedProgram_PrintsHelloWorld_AndReturnsZero()
    {
        var output = new MemoryStream();

        var result = new InterpretedProgram(new Core(output)).Run(Executable);

        Assert.Equal(0u, result);
        Assert.Equal("Hello World!\n", Encoding.Latin1.GetString(output.ToArray()));
    }

    [Fact]
    public void GeneratedFile_MatchesTheTranslator()
    {
        var listing = VasmListing.Read(Path.Combine(SampleDirectory, "hello.lst"));
        var analysis = ProgramAnalysis.Analyze(HunkFile.Parse(Executable), listing);

        var expected = CSharpProgramWriter.Write(analysis, "AmigaSharp.Generated", "HelloWorld", "hello and hello.lst");

        var path = Path.Combine(TestPaths.RepositoryRoot, "AmigaSharp", "Generated", "HelloWorld.cs");
        Assert.True(expected == File.ReadAllText(path),
            "AmigaSharp/Generated/HelloWorld.cs is out of date. Run the translator again (see samples/README.md).");
    }
}
