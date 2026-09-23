using System.Text.Json;
using AmigaSharp.Runtime.Cpu;

namespace AmigaSharp.Tests.Cpu;

/// <summary>
/// Compares the decoder with the official 68000 opcode map from https://github.com/SingleStepTests/680x0.
/// Run scripts/fetch-cpu-tests.sh to download the map.
/// </summary>
public class OpcodeMapTests
{
    [Fact]
    public void Decoder_MatchesOfficialOpcodeMap()
    {
        var path = Path.Combine(TestPaths.RepositoryRoot, "build", "cpu-tests", "68000.official.json");
        if (!File.Exists(path))
            Assert.Skip("The opcode map is not downloaded. Run scripts/fetch-cpu-tests.sh.");

        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        var failures = new List<string>();
        for (var opcode = 0; opcode < 0x10000; opcode++)
        {
            var expected = map[opcode.ToString("x4")];
            // The extension words are zero. The decoder does not use their values to decide if an opcode is legal.
            var instruction = Decoder.Decode(0x1000, address => address == 0x1000 ? (ushort)opcode : (ushort)0);
            var problem = Check(expected, instruction);
            if (problem != null)
                failures.Add($"${opcode:X4}: map \"{expected}\", decoder \"{instruction}\": {problem}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} opcodes differ:\n" + string.Join("\n", failures.Take(20)));
    }

    private static string? Check(string expected, Instruction instruction)
    {
        var legal = instruction.Operation is not (Operation.Illegal or Operation.LineA or Operation.LineF);
        if (expected == "None")
            return legal ? "the map says illegal" : null;
        if (!legal)
            return "the decoder says illegal";

        var name = expected.Split(' ')[0];
        var parts = name.Split('.');
        if (!ExpectedOperations(parts[0]).Contains(instruction.Operation))
            return "the operation is different";

        if (parts.Length == 2 && parts[1] != "q")
        {
            var size = parts[1] switch { "b" => Size.Byte, "w" => Size.Word, _ => Size.Long };
            if (size != instruction.Size)
                return "the size is different";
        }

        return null;
    }

    private static Operation[] ExpectedOperations(string name)
    {
        return name switch
        {
            "ADD" => [Operation.Add, Operation.Addi, Operation.Addq],
            "SUB" => [Operation.Sub, Operation.Subi, Operation.Subq],
            "AND" or "ANDItoCCR" or "ANDItoSR" => [Operation.And, Operation.Andi],
            "OR" or "ORItoCCR" or "ORItoSR" => [Operation.Or, Operation.Ori],
            "EOR" or "EORItoCCR" or "EORItoSR" => [Operation.Eor, Operation.Eori],
            "CMP" => [Operation.Cmp, Operation.Cmpi, Operation.Cmpm],
            "MOVE" or "MOVEfromSR" or "MOVEtoSR" or "MOVEtoCCR" or "MOVEfromUSP" or "MOVEtoUSP" =>
                [Operation.Move, Operation.Moveq],
            "UNLINK" => [Operation.Unlk],
            _ => [Enum.Parse<Operation>(name, ignoreCase: true)],
        };
    }
}
