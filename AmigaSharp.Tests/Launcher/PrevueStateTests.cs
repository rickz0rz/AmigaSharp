using AmigaSharp.PrevueLauncher;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;
using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Launcher;

public class PrevueStateTests
{
    private const uint Opaque = 0xFF10_2030;
    private const uint Transparent = 0x0000_0000;

    [Theory]
    [InlineData(true, true, "video")]
    [InlineData(true, false, "promo-right")]
    [InlineData(false, true, "promo-left")]
    [InlineData(false, false, "logo")]
    public void TopHalf_ComesFromTheGenlockKey(bool leftClear, bool rightClear, string expected)
    {
        var pixels = new uint[Display.Width * Display.Height];
        for (var y = 0; y < Display.Height / 2; y++)
        {
            for (var x = 0; x < Display.Width; x++)
                pixels[y * Display.Width + x] = (x < Display.Width / 2 ? leftClear : rightClear) ? Transparent : Opaque;
        }

        Assert.Equal(expected, PrevueState.ClassifyTopHalf(pixels));
    }

    [Fact]
    public void TopHalf_WithAMixOfBoth_IsOther()
    {
        var pixels = new uint[Display.Width * Display.Height];
        // Stripes of 8 rows: half of each area is transparent.
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = i / Display.Width / 8 % 2 == 0 ? Transparent : Opaque;

        Assert.Equal("other", PrevueState.ClassifyTopHalf(pixels));
    }

    [Fact]
    public void LogoList_HasTheLinesOfTheFile_WithTheirNumbers()
    {
        var memory = new Memory();
        var esq = FakeEsq(memory, "Logos/tvgsport.uv,\r\n\r\nLogos/KSIN!\r\n");

        var list = PrevueState.LogoList(memory, esq);

        Assert.Equal([(1, "Logos/tvgsport.uv", false), (3, "Logos/KSIN!", true)], list);
    }

    [Fact]
    public void KnownOffsets_AreTheOffsetsOfTheListing()
    {
        var path = Path.Combine(TestPaths.RepositoryRoot, "build", "target", "ESQ.lst");
        if (!File.Exists(path))
            Assert.Skip("The target listing is not built. Run scripts/build-target.sh.");
        var symbols = VasmListing.Read(path).Symbols;

        foreach (var (name, (section, offset)) in EsqVariables.KnownOffsets)
        {
            Assert.Equal(section, symbols[name].Section);
            Assert.Equal(offset, symbols[name].Value);
        }
    }

    [Fact]
    public void Variables_OfAnUnknownProgram_WithoutAListing_AreNotKnown()
    {
        Assert.Null(EsqVariables.Find([0x00, 0x00, 0x03, 0xF3], listing: null));
    }

    /// <summary>The variables of the known build of ESQ with its hunks at $40000 and $80000, and a logo list.</summary>
    private static EsqVariables FakeEsq(Memory memory, string logoList)
    {
        var variables = EsqVariables.FromBases([0x40000, 0x80000])!;
        var bytes = System.Text.Encoding.Latin1.GetBytes(logoList);
        for (var i = 0; i < bytes.Length; i++)
            memory.Write8(0x2000u + (uint)i, bytes[i]);
        memory.Write32(variables[EsqVariables.LogoListData], 0x2000);
        memory.Write32(variables[EsqVariables.LogoListSize], (uint)bytes.Length);
        return variables;
    }
}
