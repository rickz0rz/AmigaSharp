using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Graphics;
using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Translator;

/// <summary>Tests with samples/Graphics: 68000 code that draws with graphics.library.</summary>
public class GraphicsSampleTests
{
    private static readonly string SampleDirectory = Path.Combine(TestPaths.RepositoryRoot, "samples", "Graphics");
    private static readonly byte[] Executable = File.ReadAllBytes(Path.Combine(SampleDirectory, "graphics"));

    private static readonly Lazy<Type> TranslatedType = new(() =>
    {
        var listing = VasmListing.Read(Path.Combine(SampleDirectory, "graphics.lst"));
        var analysis = ProgramAnalysis.Analyze(HunkFile.Parse(Executable), listing);
        var source = CSharpProgramWriter.Write(analysis, "Samples", "Graphics", "graphics");
        return TestCompiler.Compile(source, "Graphics").GetType("Samples.Graphics")!;
    });

    [Fact]
    public void TranslatedAndInterpreted_DrawTheSamePixels()
    {
        var translated = RunTranslated();
        var interpreted = Run(core => new InterpretedProgram(core));

        Assert.Equal(PlanarImage.ToText(interpreted, 0, 0, 96, 32), PlanarImage.ToText(translated, 0, 0, 96, 32));
    }

    [Fact]
    public void Drawing_HasTheExpectedPixels()
    {
        var pens = RunTranslated();

        // The rectangle, and the rectangle after the inversion.
        Assert.Equal(1, pens[5, 5]);
        Assert.Equal(2, pens[10, 15]);
        Assert.Equal(3, pens[14, 25]);
        // The ends of the line, and a point of the line in the inverted area.
        Assert.Equal(2, pens[2, 24]);
        Assert.Equal(2, pens[18, 40]);
        Assert.Equal(1, pens[8, 30]);
        // The copy of the top-left area.
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 24; x++)
                Assert.Equal(pens[y, x], pens[16 + y, 64 + x]);
        }
    }

    [Fact]
    public void Text_HasTheGlyphsOfTheBuiltInFont()
    {
        var pens = RunTranslated();

        var glyph = BuiltInFont.Glyphs.Slice(('H' - BuiltInFont.FirstCharacter) * 8, 8);
        for (var row = 0; row < 8; row++)
        {
            for (var column = 0; column < 8; column++)
            {
                var set = (glyph[row] & (0x80 >> column)) != 0;
                Assert.Equal(set ? 3 : 0, pens[4 + row, 44 + column]);
            }
        }
    }

    private static int[,] RunTranslated() =>
        Run(core => (TranslatedProgram)Activator.CreateInstance(TranslatedType.Value, core)!);

    private static int[,] Run(Func<Core, TranslatedProgram> create)
    {
        var core = new Core(new MemoryStream(), new MemoryStream()) { Log = TextWriter.Null };
        var bitMap = create(core).Run(Executable);
        Assert.NotEqual(0u, bitMap);
        return PlanarImage.ReadPens(core.Memory, bitMap);
    }
}
