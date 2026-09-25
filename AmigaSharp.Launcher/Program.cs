using AmigaSharp.Launcher;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Graphics;
using AmigaSharp.Runtime.Hardware;

const string usage = """
    Usage: AmigaSharp.Launcher <executable> [options]

    Translates an AmigaOS executable, compiles it and runs it. The picture of the display shows in a window.

    Options:
      --listing <file.lst>      The vasm listing of the executable. The translator uses its instructions and labels.
      --interpret               Run the program in the interpreter. Do not translate it.
      --drive <directory>       The host directory of SYS:. The default is the directory of the executable.
      --volume <NAME>=<dir>     A volume on a host directory, for example DH1=/path/to/drive.
      --assign <NAME>=<path>    An assign to an AmigaDOS directory, for example DF0=DH1: or FONTS=SYS:fonts.
      --arguments <text>        The command line arguments of the program.
      --command-name <name>     The name of the command. The default is the name of the executable.
      --serial-port <port>      The TCP port of the serial bridge. The default is 5400. 0 turns the bridge off.
      --scale <n>               The size of the window: 1 is 768 by 480 pixels. The default is 1.
      --screenshot <file.png>   Do not open a window. Save the picture after --seconds, and stop.
      --seconds <n>             The time before the screenshot. The default is 10.
      --trace                   Write each library call to the standard error stream.
    """;

string? executablePath = null, listing = null, drive = null, arguments = "", commandName = null, screenshot = null;
var volumes = new List<(string Name, string Path)>();
var assigns = new List<(string Name, string Path)>();
var interpret = false;
var trace = false;
var serialPort = 5400;
var scale = 1;
var seconds = 10.0;

try
{
    for (var i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
        (string, string) Pair()
        {
            var value = Next();
            var equals = value.IndexOf('=');
            return equals > 0 ? (value[..equals], value[(equals + 1)..]) : throw new ArgumentException($"{value} is not NAME=value.");
        }

        switch (args[i])
        {
            case "--listing": listing = Next(); break;
            case "--interpret": interpret = true; break;
            case "--drive": drive = Next(); break;
            case "--volume": volumes.Add(Pair()); break;
            case "--assign": assigns.Add(Pair()); break;
            case "--arguments": arguments = Next(); break;
            case "--command-name": commandName = Next(); break;
            case "--serial-port": serialPort = int.Parse(Next()); break;
            case "--scale": scale = int.Parse(Next()); break;
            case "--screenshot": screenshot = Next(); break;
            case "--seconds": seconds = double.Parse(Next()); break;
            case "--trace": trace = true; break;
            case "--help" or "-h":
                Console.WriteLine(usage);
                return 0;
            case var value when !value.StartsWith("--") && executablePath == null: executablePath = value; break;
            default: throw new ArgumentException($"unknown argument {args[i]}.");
        }
    }

    if (executablePath == null)
        throw new ArgumentException("the executable is missing.");
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    Console.Error.WriteLine(usage);
    return 2;
}

var log = Console.Error;
var executable = File.ReadAllBytes(executablePath);
drive ??= Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
commandName ??= Path.GetFileName(executablePath);

var core = new Core(rootDirectory: drive) { TraceLibraryCalls = trace };
foreach (var (name, path) in volumes)
    core.FileSystem.AddVolume(name, path);
foreach (var (name, path) in assigns)
    core.FileSystem.AddAssign(name, path);

// RAM: is a new directory for each run. T: is RAM:T, as in the Startup-Sequence of Workbench.
var ram = Directory.CreateTempSubdirectory("AmigaSharp-RAM-").FullName;
Directory.CreateDirectory(Path.Combine(ram, "T"));
core.FileSystem.AddVolume("RAM", ram);
if (assigns.All(assign => !assign.Name.Equals("T", StringComparison.OrdinalIgnoreCase)))
    core.FileSystem.AddAssign("T", "RAM:T");

using var bridge = serialPort > 0 ? new TcpSerialBridge(serialPort, log: log) : null;
if (bridge != null)
{
    core.Chipset.Custom.Serial.Connection = bridge;
    log.WriteLine($"Serial bridge on localhost:{bridge.Port}. For example: nc localhost {bridge.Port}");
}

TranslatedProgram program = interpret
    ? new InterpretedProgram(core)
    : (TranslatedProgram)Activator.CreateInstance(ProgramCompiler.Compile(executable, listing, log), core)!;

// The program runs on its own thread. The main thread shows the window, or waits for the screenshot.
var finished = false;
var exitCode = 0;
var runner = new Thread(() =>
{
    try
    {
        var result = program.Run(executable, arguments, commandName);
        log.WriteLine($"The program returned {(int)result}.");
        exitCode = (int)result;
    }
    catch (Exception e)
    {
        log.WriteLine($"The program stopped: {e.GetType().Name}: {e.Message}");
        log.WriteLine($"PC ${core.Cpu.Pc:X6}, A7 ${core.Cpu.A[7]:X6}");
        exitCode = 1;
    }
    finally
    {
        finished = true;
    }
}, 64 * 1024 * 1024) { IsBackground = true, Name = "68000" };
runner.Start();

try
{
    if (screenshot != null)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (!finished && DateTime.Now < end)
            Thread.Sleep(50);
        var pixels = new uint[Display.Width * Display.Height];
        core.Chipset.Display.CopyFrame(pixels);
        File.WriteAllBytes(screenshot, Png.Encode(Display.Width, Display.Height, pixels));
        log.WriteLine($"Saved {screenshot} (frame {core.Chipset.Display.FrameNumber}).");
    }
    else
    {
        new DisplayWindow(core.Chipset.Display, $"AmigaSharp: {commandName}", scale).Run(() => finished);
    }
}
finally
{
    Directory.Delete(ram, recursive: true);
}

return exitCode;
