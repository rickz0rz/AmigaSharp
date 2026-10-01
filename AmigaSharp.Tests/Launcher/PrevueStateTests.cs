using AmigaSharp.PrevueLauncher;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Exec;
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
        var pixels = new uint[Display.Width * Display.HeightOf(VideoStandard.Ntsc)];
        for (var y = 0; y < Display.HeightOf(VideoStandard.Ntsc) / 2; y++)
        {
            for (var x = 0; x < Display.Width; x++)
                pixels[y * Display.Width + x] = (x < Display.Width / 2 ? leftClear : rightClear) ? Transparent : Opaque;
        }

        Assert.Equal(expected, PrevueState.ClassifyTopHalf(pixels));
    }

    [Theory]
    // A channel logo of the listings tool: a card on the left, and text on the right, on the genlock key.
    [InlineData(0.30, 0.94, "logo")]
    // A logo of the drive with no genlock key.
    [InlineData(0.02, 0.02, "logo")]
    // One half is clear, and the other half has only some content: not a promo.
    [InlineData(1.0, 0.5, "other")]
    [InlineData(0.5, 1.0, "other")]
    public void TopHalf_WithSomeKeyInTheHalves(double leftKey, double rightKey, string expected)
    {
        var pixels = new uint[Display.Width * Display.HeightOf(VideoStandard.Ntsc)];
        for (var y = 0; y < Display.HeightOf(VideoStandard.Ntsc) / 2; y++)
        {
            for (var x = 0; x < Display.Width; x++)
            {
                // The key is spread evenly over each half, in a pattern of 100 pixels.
                var part = (x * 7 + y * 13) % 100 / 100.0;
                pixels[y * Display.Width + x] = part < (x < Display.Width / 2 ? leftKey : rightKey) ? Transparent : Opaque;
            }
        }

        Assert.Equal(expected, PrevueState.ClassifyTopHalf(pixels));
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
    public void LoadLogo_TakesTheLoadedLogoFromEsq_AndFreesItLater()
    {
        var core = new Core(new MemoryStream()) { Log = TextWriter.Null };
        var allocator = core.Allocator;
        var memory = core.Memory;
        // The variables of ESQ are in a block of memory, so that the logo does not use the same memory.
        var block = allocator.Allocate(0x10000, MemoryFlags.Any | MemoryFlags.Clear);
        var esq = EsqVariables.FromBases([block, block])!;
        var list = allocator.Allocate(0x100, MemoryFlags.Any);
        var bytes = System.Text.Encoding.Latin1.GetBytes("Logos/KTIVDT\r\nLogos/Enews.uv,\r\nLogos/Insider.uv,\r\n");
        for (var i = 0; i < bytes.Length; i++)
            memory.Write8(list + (uint)i, bytes[i]);
        memory.Write32(esq[EsqVariables.LogoListData], list);
        memory.Write32(esq[EsqVariables.LogoListSize], (uint)bytes.Length);
        memory.Write32(esq[EsqVariables.BytesAllocated], 1000);
        memory.Write32(esq[EsqVariables.FreeCount], 5);
        var free = allocator.Available(MemoryFlags.Any);

        // A loaded logo as ESQ makes it: a node with its name, two rasters of 64 by 10 pixels, and one other node.
        var node = allocator.Allocate(372, MemoryFlags.Any | MemoryFlags.Clear);
        foreach (var (value, i) in "KTIVDT".Select((value, i) => (value, i)))
            memory.Write8(node + (uint)i, (byte)value);
        memory.Write16(node + 176, 64);
        memory.Write16(node + 178, 10);
        memory.Write8(node + 184, 2);
        for (var i = 0u; i < 2; i++)
            memory.Write32(node + 0x90 + 4 * i, allocator.Allocate(8 * 10, MemoryFlags.Chip));
        memory.Write32(node + 364, allocator.Allocate(12, MemoryFlags.Any | MemoryFlags.Clear));
        memory.Write32(esq[EsqVariables.LoadedLogo], node);
        memory.Write32(esq[EsqVariables.LoadedLogoCount], 1);

        var now = DateTime.UtcNow;
        var state = new PrevueState(core, new ControlLineFeed(), new ControlLineRequests(new ControlLineFeed()), esq,
            now: () => now);
        state.LoadLogo(3);
        core.PollNow();

        Assert.Equal(0u, memory.Read32(esq[EsqVariables.LoadedLogo]));
        Assert.Equal(0u, memory.Read32(esq[EsqVariables.LoadedLogoCount]));
        Assert.Equal(2, memory.Read16(esq[EsqVariables.LogoListLine]));
        Assert.True(allocator.Available(MemoryFlags.Any) < free);

        now += TimeSpan.FromSeconds(11);
        core.PollNow();

        Assert.Equal(free, allocator.Available(MemoryFlags.Any));
        Assert.Equal(1000u - 372 - 12, memory.Read32(esq[EsqVariables.BytesAllocated]));
        Assert.Equal(7u, memory.Read32(esq[EsqVariables.FreeCount]));
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
