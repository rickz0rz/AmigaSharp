using AmigaSharp.Runtime.Cpu;

namespace AmigaSharp.Tests.Cpu;

/// <summary>Runs the 68000 test vectors through the interpreter.</summary>
public class ProcessorVectorTests
{
    public static TheoryData<string> Files() => VectorSuite.Files();

    [Theory]
    [MemberData(nameof(Files))]
    public void Vectors(string name)
    {
        var tests = VectorSuite.Load(name);

        var result = VectorSuite.Run(tests, (cpu, _) => new Interpreter(cpu).Step());

        Assert.True(result.Failures.Count == 0, result.Describe(name));
    }
}
