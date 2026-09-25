using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Translator;

/// <summary>
/// Tests with samples/Tasks: a program that starts a second process with CreateProc. The two tasks wait for each other
/// in busy loops, and the child signals the main task at the end.
/// </summary>
public class TasksSampleTests
{
    private static readonly string SampleDirectory = Path.Combine(TestPaths.RepositoryRoot, "samples", "Tasks");
    private static readonly byte[] Executable = File.ReadAllBytes(Path.Combine(SampleDirectory, "tasks"));

    private static readonly Lazy<Type> TranslatedType = new(() =>
    {
        var listing = VasmListing.Read(Path.Combine(SampleDirectory, "tasks.lst"));
        var analysis = ProgramAnalysis.Analyze(HunkFile.Parse(Executable), listing);
        var source = CSharpProgramWriter.Write(analysis, "Samples", "Tasks", "tasks");
        return TestCompiler.Compile(source, "Tasks").GetType("Samples.Tasks")!;
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoTasks_ShareTheCpu(bool translated)
    {
        var core = new Core(new MemoryStream(), new MemoryStream(), Path.GetTempPath()) { Log = TextWriter.Null };
        var program = translated
            ? (TranslatedProgram)Activator.CreateInstance(TranslatedType.Value, core)!
            : new InterpretedProgram(core);

        var result = RunWithTimeout(() => program.Run(Executable), TimeSpan.FromSeconds(20));

        Assert.Equal(1000u, result);
        // The child ended, so only the main task is left.
        Assert.Equal([core.MainProcess], core.Scheduler.Tasks);
    }

    private static uint RunWithTimeout(Func<uint> run, TimeSpan timeout)
    {
        var task = Task.Factory.StartNew(run, TaskCreationOptions.LongRunning);
        Assert.True(task.Wait(timeout), "The program did not end. The tasks do not share the CPU.");
        return task.Result;
    }
}
