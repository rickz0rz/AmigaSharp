using System.Text;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Cpu;
using AmigaSharp.Tests.Cpu;
using AmigaSharp.Translator;
using Decoder = AmigaSharp.Runtime.Cpu.Decoder;

namespace AmigaSharp.Tests.Translator;

/// <summary>
/// Runs a sample of the 68000 test vectors through the code that <see cref="InstructionEmitter"/> writes. The
/// translated code must give the same result as the interpreter.
/// </summary>
public class EmitterVectorTests
{
    // The number of vectors from each file. Each vector becomes a C# method.
    private const int SampleSize = 250;

    public static TheoryData<string> Files() => VectorSuite.Files();

    [Theory]
    [MemberData(nameof(Files))]
    public void Vectors(string name)
    {
        var tests = VectorSuite.Load(name).Take(SampleSize).ToList();
        var methods = CompileMethods(name, tests);

        var result = VectorSuite.Run(tests, (cpu, test) =>
        {
            try
            {
                methods[test.Name](cpu, cpu.Memory);
            }
            catch (CpuTrapException trap)
            {
                // The translated code sets PC to the address that the exception stacks.
                new Interpreter(cpu).RaiseException(trap.Vector, cpu.Pc);
                return;
            }

            // The 68000 fetches the next instruction at once. A branch to an odd address fails at this point.
            if ((cpu.Pc & 1) != 0)
                throw new AddressErrorException(cpu.Pc & Memory.AddressMask);
        });

        Assert.True(result.Failures.Count == 0, result.Describe(name));
    }

    private static Dictionary<string, Action<CpuState, Memory>> CompileMethods(string name, List<VectorSuite.VectorTest> tests)
    {
        var emitter = new InstructionEmitter(new VectorFlow());
        var source = new StringBuilder();
        source.AppendLine("using AmigaSharp.Runtime;");
        source.AppendLine("using AmigaSharp.Runtime.Cpu;");
        source.AppendLine("public static class Vectors");
        source.AppendLine("{");

        var memory = new Memory(guardHardware: false);
        for (var n = 0; n < tests.Count; n++)
        {
            var test = tests[n];
            VectorSuite.WriteInitialMemory(memory, test);
            var instruction = Decoder.Decode(test.Initial.Pc, memory.Read16);

            source.AppendLine($"    // {test.Name}: {instruction}");
            source.AppendLine($"    public static void T{n}(CpuState cpu, Memory memory)");
            source.AppendLine("    {");
            source.AppendLine($"        {InstructionEmitter.SetPc(instruction.NextAddress)}");
            source.AppendLine("        {");
            foreach (var line in emitter.Emit(instruction))
                source.AppendLine($"            {line}");
            source.AppendLine("        }");
            source.AppendLine("    }");
        }

        source.AppendLine("}");

        var type = TestCompiler.Compile(source.ToString(), "Vectors_" + name.Replace('.', '_')).GetType("Vectors")!;
        var methods = new Dictionary<string, Action<CpuState, Memory>>();
        for (var n = 0; n < tests.Count; n++)
            methods[tests[n].Name] = type.GetMethod($"T{n}")!.CreateDelegate<Action<CpuState, Memory>>();
        return methods;
    }

    /// <summary>Control flow for one instruction: a transfer sets PC and returns.</summary>
    private sealed class VectorFlow : IControlFlow
    {
        public string Jump(Instruction instruction, uint target) =>
            $"{{ {InstructionEmitter.SetPc(target)} return; }}";

        public string JumpDynamic(Instruction instruction, string target) => $"{{ cpu.Pc = {target}; return; }}";

        public string Call(Instruction instruction, uint target) =>
            $"{{ cpu.Push32({InstructionEmitter.Hex(instruction.NextAddress)}); {InstructionEmitter.SetPc(target)} return; }}";

        public string CallDynamic(Instruction instruction, string target) =>
            $"{{ cpu.Push32({InstructionEmitter.Hex(instruction.NextAddress)}); cpu.Pc = {target}; return; }}";

        public string Return(Instruction instruction, string address) => $"{{ cpu.Pc = {address}; return; }}";
    }
}
