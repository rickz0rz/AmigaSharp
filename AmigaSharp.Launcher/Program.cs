using AmigaSharp.Launcher;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Graphics;
using AmigaSharp.Runtime.Hardware;

const string usage = """
    Usage: AmigaSharp.Launcher <executable> [options]

    Translates an AmigaOS executable, compiles it and runs it. The picture of the display shows in a window.

    Usage: AmigaSharp.Launcher unpack <file>... --output <directory>

    Unpacks PowerPacker files (files that start with PP20) to the directory.

    Options:
      --listing <file.lst>      The vasm listing of the executable. The translator uses its instructions and labels.
      --interpret               Run the program in the interpreter. Do not translate it.
      --drive <directory>       The host directory of SYS:. The default is the directory of the executable.
      --volume <NAME>=<dir>     A volume on a host directory, for example DH1=/path/to/drive.
      --assign <NAME>=<path>    An assign to an AmigaDOS directory, for example DF0=DH1: or FONTS=SYS:fonts.
      --arguments <text>        The command line arguments of the program.
      --command-name <name>     The name of the command. The default is the name of the executable.
      --serial-port <port>      The TCP port of the serial bridge. The default is 5400. 0 turns the bridge off.
      --serial-file <file>      Replay a captured feed on the serial port, in place of the bridge. The replay starts
                                when the program enables the RBF interrupt, and it goes at the baud rate of SERPER.
      --serial-start <seconds>  The time of the Amiga clock before the replay can start. The default is 0. ESQ empties
                                its receive buffer while it starts, so give it time: for example 8.
      --serial-speed <n>        Receive the serial bytes n times faster than the baud rate of SERPER. The default is 1.
                                ESQ has no flow control: a factor that is too large fills its receive buffer.
      --serial-log <file>       Write each serial byte in the two directions to the file, with the time.
      --feed-trace <file>       Write the commands that the ESQ feed parser reads, and the changes of its counters,
                                to the file. This option needs --listing.
      --scale <n>               The size of the window: 1 is 768 by 480 pixels. The default is 1.
      --screenshot <file.png>   Do not open a window. Save the picture after --seconds, and stop.
      --seconds <n>             The time before the screenshot. The default is 10.
      --date <date>             The date and the time of the Amiga at the start, for example 2020-11-01T16:00. The
                                default is the time of the host.
      --virtual-time            Use a virtual clock: time moves at each safe point, and waits end at once. A run is then
                                the same each time, and it is as fast as the host can run it.
      --press <seconds>=<key>   Press a key at a time, for example 5=escape or 7.5=f1. The names are the letters, the
                                digits, space, return, escape, backspace, tab, delete, help, up, down, left, right and
                                f1 to f10. The option can occur more than once.
      --copper-dump <seconds>=<file>
                                Write the copper writes of the first frame after the time to the file. The chip
                                memory goes to the file with the name <file>.chip.
      --fast-cpu                Run the 68000 as fast as the host can. By default, it runs at the speed of a real
                                68000 (7.16 MHz), and it sleeps when it is ahead.
      --turbo <seconds>         Run the 68000 as fast as the host can for the first seconds of Amiga time, and then
                                at the speed of a real 68000. The start of a program is then faster.
      --turbo-until <label>     Run the 68000 as fast as the host can until the word at the label of the listing is
                                not 0, and then at the speed of a real 68000. For ESQ, the label
                                _ESQ_MainLoopUiTickEnabledFlag becomes 1 when its main loop starts.
      --stats                   Write the speed of the emulation each second: the frames that the display made and
                                dropped, the time to make a frame, and the time that the program waited.
      --trace                   Write each library call to the standard error stream.
    """;

if (args.Length > 0 && args[0] == "unpack")
    return Unpack(args[1..]);

string? executablePath = null, listing = null, drive = null, arguments = "", commandName = null, screenshot = null;
string? serialFile = null, serialLog = null, feedTrace = null;
var serialStart = 0.0;
var serialSpeed = 1.0;
var volumes = new List<(string Name, string Path)>();
var assigns = new List<(string Name, string Path)>();
var interpret = false;
var trace = false;
var serialPort = 5400;
var scale = 1;
var seconds = 10.0;
var virtualTime = false;
var stats = false;
var fastCpu = false;
double? turboSeconds = null;
string? turboLabel = null;
DateTime? date = null;
var presses = new List<(double Seconds, byte RawKey)>();
(double Seconds, string Path)? copperDump = null;

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
            case "--serial-file": serialFile = Next(); break;
            case "--serial-start": serialStart = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--serial-log": serialLog = Next(); break;
            case "--serial-speed": serialSpeed = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--feed-trace": feedTrace = Next(); break;
            case "--scale": scale = int.Parse(Next()); break;
            case "--screenshot": screenshot = Next(); break;
            case "--seconds": seconds = double.Parse(Next()); break;
            case "--trace": trace = true; break;
            case "--stats": stats = true; break;
            case "--fast-cpu": fastCpu = true; break;
            case "--turbo": turboSeconds = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--turbo-until": turboLabel = Next(); break;
            case "--virtual-time": virtualTime = true; break;
            case "--date": date = DateTime.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--copper-dump":
            {
                var (time, path) = Pair();
                copperDump = (double.Parse(time, System.Globalization.CultureInfo.InvariantCulture), path);
                break;
            }
            case "--press":
            {
                var (time, key) = Pair();
                presses.Add((double.Parse(time, System.Globalization.CultureInfo.InvariantCulture),
                    KeyNames.RawKey(key) ?? throw new ArgumentException($"{key} is not a key name.")));
                break;
            }
            case "--help" or "-h":
                Console.WriteLine(usage);
                return 0;
            case var value when !value.StartsWith("--") && executablePath == null: executablePath = value; break;
            default: throw new ArgumentException($"unknown argument {args[i]}.");
        }
    }

    if (executablePath == null)
        throw new ArgumentException("the executable is missing.");
    if (feedTrace != null && listing == null)
        throw new ArgumentException("--feed-trace needs --listing.");
    if (turboLabel != null && listing == null)
        throw new ArgumentException("--turbo-until needs --listing.");
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

var realTimeClock = virtualTime ? null : new RealTimeClock(start: false);
IClock clock = realTimeClock ?? (IClock)new VirtualClock();
var measuringClock = stats ? new MeasuringClock(clock) : null;
clock = measuringClock ?? clock;
var turbo = turboSeconds != null || turboLabel != null;
var core = new Core(rootDirectory: drive, clock: clock) { TraceLibraryCalls = trace, PaceCpu = !fastCpu && !turbo };
if (date != null)
    core.SetDate(date.Value);
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

using var bridge = serialPort > 0 && serialFile == null ? new TcpSerialBridge(serialPort, log: log) : null;
if (bridge != null)
{
    core.Chipset.Custom.Serial.Connection = bridge;
    log.WriteLine($"Serial bridge on localhost:{bridge.Port}. For example: nc localhost {bridge.Port}");
}

if (serialFile != null)
{
    var feed = File.ReadAllBytes(serialFile);
    var custom = core.Chipset.Custom;
    core.Chipset.Custom.Serial.Connection = new ReplaySerialConnection(feed,
        () => clock.Elapsed.TotalSeconds >= serialStart && (custom.Intena & (1 << InterruptBit.Rbf)) != 0);
    log.WriteLine($"Replaying {feed.Length} bytes from {serialFile} on the serial port.");
}

core.Chipset.Custom.Serial.SpeedFactor = serialSpeed;

using var serialLogWriter = serialLog == null ? null : new StreamWriter(serialLog);
using var loggingConnection = serialLogWriter == null
    ? null
    : new LoggingSerialConnection(core.Chipset.Custom.Serial.Connection, serialLogWriter,
        () => clock.Elapsed);
if (loggingConnection != null)
    core.Chipset.Custom.Serial.Connection = loggingConnection;

using var feedTraceWriter = feedTrace == null ? null : new StreamWriter(feedTrace);

TranslatedProgram program = interpret
    ? new InterpretedProgram(core)
    : (TranslatedProgram)Activator.CreateInstance(ProgramCompiler.Compile(executable, listing, log), core)!;

if (turbo && !fastCpu)
{
    // The launcher checks the end of the turbo at each safe point. The label is a word in the memory of the program.
    uint? turboAddress = null;
    if (turboLabel != null)
    {
        var symbols = AmigaSharp.Translator.VasmListing.Read(listing!).Symbols;
        var bases = AmigaSharp.Runtime.Loader.HunkLayout.Assign(AmigaSharp.Runtime.Loader.HunkFile.Parse(executable));
        if (!symbols.TryGetValue(turboLabel, out var symbol) || symbol.Section is not { } section || section >= bases.Length)
        {
            Console.Error.WriteLine($"error: the listing does not have the label {turboLabel}.");
            return 2;
        }

        turboAddress = bases[section] + symbol.Value;
    }

    core.AddPollHandler(() =>
    {
        if (core.PaceCpu)
            return;
        var done = (turboSeconds != null && clock.Elapsed.TotalSeconds >= turboSeconds)
                   || (turboAddress is { } address && core.Memory.Read16(address) != 0);
        if (!done)
            return;
        core.PaceCpu = true;
        log.WriteLine($"The turbo ended at {clock.Elapsed.TotalSeconds:F1} s. The 68000 now runs at its real speed.");
    });
}

if (feedTraceWriter != null)
{
    var bases = AmigaSharp.Runtime.Loader.HunkLayout.Assign(AmigaSharp.Runtime.Loader.HunkFile.Parse(executable));
    FeedTrace.Create(core, AmigaSharp.Translator.VasmListing.Read(listing!), bases, feedTraceWriter);
}

// The program runs on its own thread. The main thread shows the window, or waits for the screenshot.
var finished = false;
var exitCode = 0;
var runner = new Thread(() =>
{
    try
    {
        realTimeClock?.Start();
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

// The scripted key presses: each key goes down, and up again 0.1 second later.
if (presses.Count > 0)
{
    new Thread(() =>
    {
        foreach (var (time, rawKey) in presses.OrderBy(press => press.Seconds))
        {
            WaitForTime(time);
            core.KeyboardInput.PostRawKey(rawKey, up: false);
            WaitForTime(time + 0.1);
            core.KeyboardInput.PostRawKey(rawKey, up: true);
        }
    }) { IsBackground = true, Name = "Key presses" }.Start();
}

if (copperDump is var (dumpTime, dumpPath))
{
    new Thread(() =>
    {
        WaitForTime(dumpTime);
        core.Chipset.Display.CopperDump = new StreamWriter(dumpPath);
        File.WriteAllBytes(dumpPath + ".chip", core.Memory.Ram(0, 0x20_0000).ToArray());
    }) { IsBackground = true, Name = "Copper dump" }.Start();
}

if (measuringClock != null)
{
    new Thread(() =>
    {
        var display = core.Chipset.Display;
        var custom = core.Chipset.Custom;
        var host = System.Diagnostics.Stopwatch.StartNew();
        var delivered = core.Interrupts.Delivered;
        var (lastHost, lastAmiga, lastFrames, lastDropped, lastRender, lastWait) =
            (TimeSpan.Zero, TimeSpan.Zero, 0L, 0L, TimeSpan.Zero, TimeSpan.Zero);
        var (lastVertb, lastAudio1) = (0L, 0L);
        while (!finished)
        {
            Thread.Sleep(1000);
            var (now, amiga, frames, dropped, render, wait) = (host.Elapsed, clock.Elapsed, display.FrameNumber,
                custom.FramesDropped, display.RenderTime, measuringClock.WaitTime);
            var seconds = (now - lastHost).TotalSeconds;
            var made = frames - lastFrames;
            log.WriteLine($"stats: {made / seconds,5:F1} frames/s, {(dropped - lastDropped) / seconds,5:F1} dropped/s, " +
                          $"{(made > 0 ? (render - lastRender).TotalMilliseconds / made : 0),5:F2} ms/frame, " +
                          $"waiting {(wait - lastWait).TotalSeconds / seconds * 100,3:F0}%, " +
                          $"Amiga time x{(amiga - lastAmiga).TotalSeconds / seconds:F2}, " +
                          $"interrupts/s VERTB {(delivered[InterruptBit.VerticalBlank] - lastVertb) / seconds:F0} " +
                          $"AUD1 {(delivered[InterruptBit.Audio0 + 1] - lastAudio1) / seconds:F0}");
            (lastVertb, lastAudio1) = (delivered[InterruptBit.VerticalBlank], delivered[InterruptBit.Audio0 + 1]);
            (lastHost, lastAmiga, lastFrames, lastDropped, lastRender, lastWait) = (now, amiga, frames, dropped, render, wait);
        }
    }) { IsBackground = true, Name = "Stats" }.Start();
}

void WaitForTime(double time)
{
    while (!finished && clock.Elapsed.TotalSeconds < time)
        Thread.Sleep(5);
}

try
{
    if (screenshot != null)
    {
        WaitForTime(seconds);
        var pixels = new uint[Display.Width * Display.Height];
        core.Chipset.Display.CopyFrame(pixels);
        File.WriteAllBytes(screenshot, Png.Encode(Display.Width, Display.Height, pixels));
        log.WriteLine($"Saved {screenshot} (frame {core.Chipset.Display.FrameNumber}).");
    }
    else
    {
        void Key(Silk.NET.SDL.Scancode scancode, bool up)
        {
            if (KeyboardMapping.TryGetRawKey(scancode, out var rawKey))
                core.KeyboardInput.PostRawKey(rawKey, up);
        }

        new DisplayWindow(core.Chipset.Display, $"AmigaSharp: {commandName}", scale, Key).Run(() => finished);
    }
}
finally
{
    Directory.Delete(ram, recursive: true);
}

return exitCode;

static int Unpack(string[] arguments)
{
    var output = arguments.SkipWhile(argument => argument != "--output").Skip(1).FirstOrDefault();
    var files = arguments.TakeWhile(argument => argument != "--output").ToList();
    if (output == null || files.Count == 0)
    {
        Console.Error.WriteLine("error: unpack needs files and --output <directory>.");
        return 2;
    }

    Directory.CreateDirectory(output);
    var result = 0;
    foreach (var file in files)
    {
        var data = File.ReadAllBytes(file);
        if (!AmigaSharp.Runtime.Dos.PowerPacker.IsPacked(data))
        {
            Console.WriteLine($"{file}: not packed.");
            continue;
        }

        try
        {
            var unpacked = AmigaSharp.Runtime.Dos.PowerPacker.Unpack(data);
            File.WriteAllBytes(Path.Combine(output, Path.GetFileName(file)), unpacked);
            Console.WriteLine($"{file}: {data.Length} bytes, unpacked {unpacked.Length} bytes.");
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine($"{file}: {e.Message}");
            result = 1;
        }
    }

    return result;
}
