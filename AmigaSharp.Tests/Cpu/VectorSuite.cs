using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Cpu;

namespace AmigaSharp.Tests.Cpu;

/// <summary>
/// The SingleStepTests 68000 vectors (https://github.com/SingleStepTests/680x0). Run scripts/fetch-cpu-tests.sh to
/// download them. A test sets the initial state, runs one instruction, and compares the final registers, SR, PC and
/// RAM. It does not compare bus transactions or cycle counts.
/// </summary>
public static class VectorSuite
{
    public const int MaximumReportedFailures = 8;

    public static readonly string VectorDirectory = Path.Combine(TestPaths.RepositoryRoot, "build", "cpu-tests", "68000");

    // The runtime does not follow these vectors.
    private static readonly HashSet<string> KnownBadVectors =
    [
        // Bad data: a byte shift cannot change the upper bits of D2. See
        // https://github.com/SingleStepTests/680x0/issues/4.
        "e502 [ASL.b Q, D2] 1583",
        "e502 [ASL.b Q, D2] 1761",

        // The only divide-by-zero vector in the set. It stacks the address of the DIVU instruction. The Motorola
        // manuals say that a zero divide stacks the address of the next instruction, and the runtime does that.
        "80ef [DIVU (d16, A7), D0] 5745",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string AddressErrorMatched = "address error";

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        if (!Directory.Exists(VectorDirectory))
        {
            data.Add("(not downloaded)");
            return data;
        }

        foreach (var path in Directory.GetFiles(VectorDirectory, "*.json.gz").Order())
            data.Add(Path.GetFileName(path)[..^".json.gz".Length]);
        return data;
    }

    /// <summary>Loads the vectors of one file. Skips the test if the vectors are not downloaded.</summary>
    public static List<VectorTest> Load(string name)
    {
        var path = Path.Combine(VectorDirectory, name + ".json.gz");
        if (!File.Exists(path))
            Assert.Skip("The 68000 test vectors are not downloaded. Run scripts/fetch-cpu-tests.sh.");

        using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<VectorTest>>(stream, JsonOptions)!
            .Where(test => !KnownBadVectors.Contains(test.Name))
            .ToList();
    }

    public sealed record Result(int Total, int AddressErrors, List<string> Failures)
    {
        public string Describe(string name) =>
            $"{name}: {Failures.Count} of {Total} failed ({AddressErrors} address errors matched).\n"
            + string.Join("\n", Failures.Take(MaximumReportedFailures));
    }

    /// <summary>
    /// Runs each test. <paramref name="execute"/> runs one instruction at <see cref="CpuState.Pc"/>. It can throw
    /// <see cref="AddressErrorException"/>.
    /// </summary>
    public static Result Run(IEnumerable<VectorTest> tests, Action<CpuState, VectorTest> execute)
    {
        var memory = new Memory(guardHardware: false);
        var failures = new List<string>();
        var addressErrors = 0;
        var total = 0;
        foreach (var test in tests)
        {
            total++;
            var outcome = RunTest(memory, test, execute);
            if (outcome == AddressErrorMatched)
                addressErrors++;
            else if (outcome != null)
                failures.Add(outcome);
        }

        return new Result(total, addressErrors, failures);
    }

    /// <summary>Writes the initial memory of the test: the prefetch words and the RAM list.</summary>
    public static void WriteInitialMemory(Memory memory, VectorTest test)
    {
        var initial = test.Initial;
        // The prefetch queue holds the words at PC and PC+2. They are not in the RAM list.
        WriteWordBytes(memory, initial.Pc, initial.Prefetch[0]);
        WriteWordBytes(memory, initial.Pc + 2, initial.Prefetch[1]);
        foreach (var entry in initial.Ram)
            memory.Write8(entry[0], (byte)entry[1]);
    }

    /// <summary>Returns null if the test passes, <see cref="AddressErrorMatched"/>, or a description of the failure.</summary>
    private static string? RunTest(Memory memory, VectorTest test, Action<CpuState, VectorTest> execute)
    {
        var initial = test.Initial;
        var cpu = new CpuState(memory);
        try
        {
            cpu.Sr = initial.Sr;
            cpu.Usp = initial.Usp;
            cpu.Ssp = initial.Ssp;
            for (var i = 0; i < 8; i++)
                cpu.D[i] = initial.D(i);
            for (var i = 0; i < 7; i++)
                cpu.A[i] = initial.A(i);
            cpu.Pc = initial.Pc;
            WriteInitialMemory(memory, test);

            string? addressError = null;
            try
            {
                execute(cpu, test);
            }
            catch (AddressErrorException e)
            {
                addressError = e.Message;
            }

            if (ExpectsAddressError(test))
                return addressError != null ? AddressErrorMatched : $"{test.Name}: expected an address error";
            if (addressError != null)
                return $"{test.Name}: {addressError}";

            var differences = Compare(test.Final, cpu, memory);
            return differences.Length == 0 ? null : $"{test.Name}:{differences} (expected/actual)";
        }
        catch (Exception e) when (e is not AddressErrorException)
        {
            return $"{test.Name}: {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            // Clear the memory that this test used, so that the next test starts from zero.
            WriteWordBytes(memory, initial.Pc, 0);
            WriteWordBytes(memory, initial.Pc + 2, 0);
            foreach (var entry in initial.Ram)
                memory.Write8(entry[0], 0);
            foreach (var entry in test.Final.Ram)
                memory.Write8(entry[0], 0);
        }
    }

    private static void WriteWordBytes(Memory memory, uint address, ushort value)
    {
        memory.Write8(address, (byte)(value >> 8));
        memory.Write8(address + 1, (byte)value);
    }

    /// <summary>An address error jumps to the handler in vector 3, at address $0C.</summary>
    private static bool ExpectsAddressError(VectorTest test)
    {
        uint handler = 0;
        for (uint address = 12; address < 16; address++)
        {
            var entry = test.Initial.Ram.FirstOrDefault(e => e[0] == address);
            handler = handler << 8 | (entry?[1] ?? 0);
        }

        return test.Final.Pc == handler && test.Initial.Ram.Any(e => e[0] is >= 12 and < 16);
    }

    private static string Compare(CpuStateJson expected, CpuState cpu, Memory memory)
    {
        var text = new StringBuilder();
        for (var i = 0; i < 8; i++)
            Check(text, $"D{i}", expected.D(i), cpu.D[i]);
        for (var i = 0; i < 7; i++)
            Check(text, $"A{i}", expected.A(i), cpu.A[i]);
        Check(text, "USP", expected.Usp, cpu.Usp);
        Check(text, "SSP", expected.Ssp, cpu.Ssp);
        if (expected.Sr != cpu.Sr)
            text.Append($" SR ${expected.Sr:X4}/${cpu.Sr:X4} ({FlagDifferences(expected.Sr, cpu.Sr)})");
        Check(text, "PC", expected.Pc, cpu.Pc);
        foreach (var entry in expected.Ram)
        {
            var actual = memory.Read8(entry[0]);
            if (actual != entry[1])
                text.Append($" [${entry[0]:X6}] ${entry[1]:X2}/${actual:X2}");
        }

        return text.ToString();
    }

    private static void Check(StringBuilder text, string name, uint expected, uint actual)
    {
        if (expected != actual)
            text.Append($" {name} ${expected:X8}/${actual:X8}");
    }

    private static string FlagDifferences(ushort expected, ushort actual)
    {
        var names = new[] { "C", "V", "Z", "N", "X" };
        var differences = new List<string>();
        for (var bit = 0; bit < 5; bit++)
        {
            if (((expected ^ actual) & (1 << bit)) != 0)
                differences.Add($"{names[bit]}={(expected >> bit) & 1}");
        }

        if (((expected ^ actual) & 0xFFE0) != 0)
            differences.Add("system byte");
        return string.Join(" ", differences);
    }

    public sealed class VectorTest
    {
        public string Name { get; set; } = "";
        public CpuStateJson Initial { get; set; } = new();
        public CpuStateJson Final { get; set; } = new();
    }

    public sealed class CpuStateJson
    {
        public uint D0 { get; set; }
        public uint D1 { get; set; }
        public uint D2 { get; set; }
        public uint D3 { get; set; }
        public uint D4 { get; set; }
        public uint D5 { get; set; }
        public uint D6 { get; set; }
        public uint D7 { get; set; }
        public uint A0 { get; set; }
        public uint A1 { get; set; }
        public uint A2 { get; set; }
        public uint A3 { get; set; }
        public uint A4 { get; set; }
        public uint A5 { get; set; }
        public uint A6 { get; set; }
        public uint Usp { get; set; }
        public uint Ssp { get; set; }
        public ushort Sr { get; set; }
        public uint Pc { get; set; }
        public ushort[] Prefetch { get; set; } = [];
        public List<uint[]> Ram { get; set; } = [];

        public uint D(int i) => i switch
        {
            0 => D0, 1 => D1, 2 => D2, 3 => D3, 4 => D4, 5 => D5, 6 => D6, _ => D7,
        };

        public uint A(int i) => i switch
        {
            0 => A0, 1 => A1, 2 => A2, 3 => A3, 4 => A4, 5 => A5, _ => A6,
        };
    }
}
