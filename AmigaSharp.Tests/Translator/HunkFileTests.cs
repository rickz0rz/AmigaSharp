using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Loader;

namespace AmigaSharp.Tests.Translator;

public class HunkFileTests
{
    private static string Sample(string directory, string name) =>
        Path.Combine(TestPaths.RepositoryRoot, "samples", directory, name);

    [Fact]
    public void Parse_ReadsCodeHunkAndRelocation()
    {
        var file = HunkFile.Read(Sample("HelloWorld", "hello"));

        var hunk = Assert.Single(file.Hunks);
        Assert.Equal(HunkType.Code, hunk.Type);
        Assert.Equal(72u, hunk.Size);
        // MOVE.L #Hello,D1 has the only absolute address.
        Assert.Equal(new Relocation(0x14, 0), Assert.Single(hunk.Relocations));
    }

    [Fact]
    public void Load_AppliesRelocationsBetweenHunks()
    {
        var file = HunkFile.Read(Sample("ControlFlow", "controlflow"));
        var memory = new Memory(guardHardware: false);
        var bases = HunkLayout.Assign(file);

        file.Load(memory, bases);

        Assert.Equal([HunkLayout.FastBase, 0x200070u], bases);
        // MOVE.L A7,SavedSp: the operand is the address of the data hunk.
        Assert.Equal(0x23CF, memory.Read16(bases[0]));
        Assert.Equal(bases[1], memory.Read32(bases[0] + 2));
    }

    [Fact]
    public void Parse_TargetExecutable_ReadsShortRelocations()
    {
        var path = Path.Combine(TestPaths.RepositoryRoot, "build", "target", "ESQ");
        if (!File.Exists(path))
            Assert.Skip("The target is not built. Run scripts/build-target.sh.");

        var file = HunkFile.Read(path);

        Assert.Equal(2, file.Hunks.Count);
        Assert.Equal((HunkType.Code, HunkMemory.Any, 211_348u), (file.Hunks[0].Type, file.Hunks[0].Memory, file.Hunks[0].Size));
        Assert.Equal((HunkType.Data, HunkMemory.Chip, 55_820u), (file.Hunks[1].Type, file.Hunks[1].Memory, file.Hunks[1].Size));
        // HUNK_DREL32 has 142 + 2829 and HUNK_RELOC32 has 319 + 5501 relocations in the code hunk.
        Assert.Equal(8791, file.Hunks[0].Relocations.Count);
        Assert.Equal(261, file.Hunks[1].Relocations.Count);
    }
}
