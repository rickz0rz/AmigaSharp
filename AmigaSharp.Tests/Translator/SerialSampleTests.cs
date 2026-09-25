using System.Net.Sockets;
using System.Text;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;
using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Translator;

/// <summary>
/// Tests with samples/Serial: 68000 code that opens serial.device, reads a line with its own RBF interrupt handler,
/// and sends the line back in upper case.
/// </summary>
public class SerialSampleTests
{
    private static readonly string SampleDirectory = Path.Combine(TestPaths.RepositoryRoot, "samples", "Serial");
    private static readonly byte[] Executable = File.ReadAllBytes(Path.Combine(SampleDirectory, "serial"));

    private static readonly Lazy<Type> TranslatedType = new(() =>
    {
        var listing = VasmListing.Read(Path.Combine(SampleDirectory, "serial.lst"));
        var analysis = ProgramAnalysis.Analyze(HunkFile.Parse(Executable), listing);
        var source = CSharpProgramWriter.Write(analysis, "Samples", "Serial", "serial");
        return TestCompiler.Compile(source, "Serial").GetType("Samples.Serial")!;
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Program_EchoesTheLineInUpperCase(bool translated)
    {
        var connection = new MemorySerialConnection();
        connection.Send(Encoding.ASCII.GetBytes("hello, amiga\n"));
        var core = NewCore(connection);

        var length = Create(core, translated).Run(Executable);

        Assert.Equal(13u, length);
        Assert.Equal("HELLO, AMIGA\n", Encoding.ASCII.GetString(connection.Sent.ToArray()));
        // SDCMD_SETPARAMS set SERPER for 19200 baud.
        Assert.Equal((ushort)(Beam.ColorClockHz / 19200 - 1), core.Chipset.Custom.Serial.Period);
        Assert.Equal(13, core.Interrupts.Delivered[InterruptBit.Rbf]);
    }

    [Fact]
    public void Program_WorksThroughTheTcpBridge()
    {
        using var bridge = new TcpSerialBridge(0);
        var core = NewCore(bridge);
        using var client = new TcpClient("127.0.0.1", bridge.Port);
        var stream = client.GetStream();
        stream.Write(Encoding.ASCII.GetBytes("tcp line\n"));

        var length = Create(core, translated: true).Run(Executable);

        var reply = new byte[9];
        stream.ReadTimeout = 5000;
        var read = 0;
        while (read < reply.Length)
            read += stream.Read(reply, read, reply.Length - read);
        Assert.Equal(9u, length);
        Assert.Equal("TCP LINE\n", Encoding.ASCII.GetString(reply));
    }

    private static Core NewCore(ISerialConnection connection)
    {
        var core = new Core(new MemoryStream(), new MemoryStream(), Path.GetTempPath()) { Log = TextWriter.Null };
        core.Chipset.Custom.Serial.Connection = connection;
        return core;
    }

    private static TranslatedProgram Create(Core core, bool translated) => translated
        ? (TranslatedProgram)Activator.CreateInstance(TranslatedType.Value, core)!
        : new InterpretedProgram(core);
}
