using AmigaSharp.Host;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Launcher;

public sealed class CodeMapTests : IDisposable
{
    private static readonly byte[] Executable =
        File.ReadAllBytes(Path.Combine(TestPaths.RepositoryRoot, "samples", "ControlFlow", "controlflow"));

    private readonly string _directory = Directory.CreateTempSubdirectory("AmigaSharp-codemap-test-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Add_KeepsCodeOfTheFile_AndTheNextLoadHasIt()
    {
        var file = HunkFile.Parse(Executable);
        var bases = HunkLayout.Assign(file);
        var memory = new Memory();
        file.Load(memory, bases);
        // The first instruction of the program. A relocation never changes an opcode, so its bytes are the file's.
        var code = bases[0];
        var changed = bases[0] + 8;
        // The program wrote other code here, for example unpacked code. It is not the code of the file.
        memory.Write16(changed, (ushort)(memory.Read16(changed) ^ 0xFFFF));

        var map = CodeMap.Load(Executable, _directory);
        var added = map.Add(Executable, memory, [code, code + 1, changed, 0x00F0_0000]);

        Assert.Equal(1, added);
        Assert.Equal([code], CodeMap.Load(Executable, _directory).Addresses);
    }

    [Fact]
    public void Merge_WritesAllTheAddressesOfTheMaps_AndReadSkipsOtherLines()
    {
        var first = Path.Combine(_directory, "first.code");
        var second = Path.Combine(_directory, "second.code");
        var merged = Path.Combine(_directory, "merged.code");
        File.WriteAllLines(first, ["# The map of the first computer.", "200010", "$22DC06"]);
        File.WriteAllLines(second, ["200010", "2628AC"]);

        var count = KnownCodeFile.Merge(merged, [first, second]);

        Assert.Equal(3, count);
        Assert.Equal(["200010", "22DC06", "2628AC"], File.ReadAllLines(merged));
        Assert.Throws<FileNotFoundException>(() => KnownCodeFile.Merge(merged, [Path.Combine(_directory, "none.code")]));
    }

    [Fact]
    public void Open_UsesTheGivenFile()
    {
        var path = Path.Combine(_directory, "program.code");
        File.WriteAllLines(path, ["200008"]);

        var map = CodeMap.Open(path);

        Assert.Equal(path, map.Path);
        Assert.Equal([0x200008u], map.Addresses);
    }

    [Fact]
    public void Disassembly_ShowsTheFunctions_TheInstructions_AndTheData()
    {
        Hunk Hunk(int index, HunkType type, byte[] data, uint size) => new()
        {
            Index = index, Type = type, Memory = HunkMemory.Any, Size = size, Data = data, Relocations = [],
        };
        // MOVEQ #1,D0; RTS; and two bytes of data. Then a BSS hunk of 8 bytes.
        var file = new HunkFile
        {
            Hunks = [Hunk(0, HunkType.Code, [0x70, 0x01, 0x4E, 0x75, 0xAB, 0xCD], 6), Hunk(1, HunkType.Bss, [], 8)],
        };
        var analysis = ProgramAnalysis.Analyze(file, listing: null);
        var start = analysis.Bases[0];

        var lines = Disassembler.Write(analysis, "test").Split('\n').Select(line => line.TrimEnd()).ToList();

        Assert.Contains($"{analysis.FunctionAt(start)!.Name}:", lines);
        Assert.Contains(lines, line => line.StartsWith($"    ${start:X6}  7001") && line.EndsWith("MOVEQ #$1,D0"));
        Assert.Contains(lines, line => line.StartsWith($"    ${start + 2:X6}  4E75") && line.EndsWith("RTS"));
        Assert.Contains($"    ${start + 4:X6}  dc.b $AB,$CD", lines);
        Assert.Contains($"    ${analysis.Bases[1]:X6}  ds.b 8", lines);
    }

    [Fact]
    public void Analysis_TranslatesKnownCode_AlsoInADataHunk()
    {
        // Hunk 0 is code that ends at once. Hunk 1 is data with code that only an address in a table can reach.
        Hunk Hunk(int index, HunkType type, byte[] data) => new()
        {
            Index = index, Type = type, Memory = HunkMemory.Any, Size = (uint)data.Length, Data = data, Relocations = [],
        };
        var file = new HunkFile
        {
            Hunks =
            [
                Hunk(0, HunkType.Code, [0x4E, 0x75]), // RTS
                Hunk(1, HunkType.Data, [0x70, 0x01, 0x4E, 0x75]), // MOVEQ #1,D0; RTS
            ],
        };
        var known = HunkLayout.Assign(file)[1];

        var without = ProgramAnalysis.Analyze(file, listing: null);
        var with = ProgramAnalysis.Analyze(file, listing: null, [known]);

        Assert.False(without.IsInstruction(known));
        Assert.True(with.IsInstruction(known));
        Assert.NotNull(with.FunctionAt(known));
    }
}
