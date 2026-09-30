using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Graphics;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Host;

/// <summary>The name, the description and the extensions of a launcher program.</summary>
/// <param name="Name">The name of the program in its usage, for example AmigaSharp.Launcher.</param>
/// <param name="Description">What the program does, for its usage.</param>
public sealed record LauncherApp(string Name, string Description, IReadOnlyList<ILauncherExtension> Extensions);

/// <summary>
/// Translates an AmigaOS executable, compiles it and runs it, and shows its display in a window, in a stream or in
/// screenshots. A launcher program (AmigaSharp.Launcher, AmigaSharp.PrevueLauncher) calls <see cref="Run"/> with its
/// extensions.
/// </summary>
public static class Launcher
{
    private const string UsageTemplate = """
        Usage: @NAME@ <executable> [options]

        @DESCRIPTION@

        Usage: @NAME@ unpack <file>... --output <directory>

        Unpacks PowerPacker files (files that start with PP20) to the directory.

        Usage: @NAME@ extract <disk.adf> --output <directory>

        Writes the files of an ADF disk image to the directory.

        The executable can be a file on a disk image: <disk.adf>:<path>, for example AmigaTestKit.adf:AmigaTestKit. The
        launcher copies the files of the disk to a new temporary directory. That directory is SYS:, DF0: and the volume
        name of the disk. The program can change these files, but not the disk image. The disk is also in the drive DF0
        for disk DMA. A write there changes only a copy in memory.

        Options:
          --listing <file.lst>      The vasm listing of the executable. The translator uses its instructions and labels.
          --interpret               Run the program in the interpreter. Do not translate it. A native (AOT) build always
                                    uses the interpreter.
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
          --scale <n>               The size of the window: 1 is 768 by 480 pixels. The default is 1.
          --deinterlace <mode>      How the window, the screenshots and the stream show an interlaced display: weave
                                    (both fields, as a TV; the default), bob (the last field, each row twice) or blend
                                    (the average of the two fields). bob and blend have no comb lines on moving content.
          --headless                Do not open a window. Run until the program ends, or until Ctrl-C.
          --stream <port>           Stream the display as live HLS video (H.264, with ffmpeg) on the HTTP port. The
                                    stream is /stream.m3u8, and /channels.m3u is a playlist for a custom channel of
                                    Channels DVR.
          --stream-4x3              Make the stream 960 by 720 pixels. The default is 1280 by 720, with bars at the sides.
          --stream-name <name>      The name of the channel in /channels.m3u. The default is the name of the command.
          --stream-audio <path>     Music for the stream: an M3U playlist, a text file with one audio file on each line,
                                    or a directory of audio files. It plays in a loop. By default, it stops while a
                                    genlock video with sound plays. The stream also has the sound of the genlock videos
                                    and of the Amiga. The HTTP server of the stream controls the sound: GET /mixer and
                                    GET /music. See the README.
          --genlock <file or URL>   Show a video behind the stream, as the genlock of Prevue: the video shows where the
                                    display has color 0 (the genlock key). A file plays in a loop. A URL plays live, for
                                    example a channel of an HDHomeRun tuner. This option needs --stream.
          --genlock-control         Start the genlock with no video: the stream shows the display over black. With this
                                    option or --genlock, the HTTP server of the stream controls the queue of videos of
                                    the genlock: GET /genlock, POST /genlock/queue, POST /genlock/next, POST /genlock/stop
                                    and DELETE /genlock/queue. See the README. This option needs --stream.
          --genlock-playlist <file> Start the genlock with the videos of a JSON file, for example {"loop": "all",
                                    "queue": [{"source": "promo.mp4"}, {"source": "black", "seconds": 180}]}. "black" is
                                    black and silence. The HTTP server controls the queue as with --genlock-control. This
                                    option needs --stream.
          --schedule <file>         Play a schedule of a JSON file: segments of videos and pauses, with the settings of
                                    the music, for example {"loop": true, "segments": [{"video": "promo.mp4"},
                                    {"pause": 180, "music": {"volume": 1}}]}. A launcher with an extension has more
                                    keys, for example "top" for Prevue. GET /schedule gives its state. This option needs
                                    --stream. See docs/orchestration.md.
          --audio-file <file.wav>   Write the sound of the audio channels to a WAV file. Without this option, the window
                                    plays the sound.
          --screenshot <file.png>   Do not open a window. Save the picture after --seconds, and stop.
          --seconds <n>             The time before the screenshot. The default is 10.
          --screenshot-every <n>    With --screenshot, also save a picture each n seconds until --seconds, with the time in
                                    the name, for example out-000120.png.
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
                                    dropped, the time to make a frame, the time that the program waited, and the free
                                    memory of the Amiga.
          --watch <label>           Write each change of the word at a label of the listing, with the time. The option can
                                    occur more than once. This option needs --listing.
          --trace                   Write each library call to the standard error stream.
        """;

    /// <summary>Runs the command line of a launcher program, and gives its exit code.</summary>
    public static int Run(string[] args, LauncherApp app)
    {
        var usage = UsageTemplate.Replace("@NAME@", app.Name).Replace("@DESCRIPTION@", app.Description) +
                    string.Concat(app.Extensions.Select(extension => "\n\n" + extension.Usage));

        if (args.Length > 0 && args[0] == "unpack")
            return Unpack(args[1..]);
        if (args.Length > 0 && args[0] == "extract")
            return Extract(args[1..]);

        LauncherOptions options;
        try
        {
            options = LauncherOptions.Parse(args, app.Extensions);
            if (options.Help)
            {
                Console.WriteLine(usage);
                return 0;
            }

            foreach (var extension in app.Extensions)
                extension.Complete(options);
            options.Check();
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            Console.Error.WriteLine(usage);
            return 2;
        }

        var executablePath = options.ExecutablePath!;
        var (listing, drive, arguments, commandName) =
            (options.Listing, options.Drive, options.Arguments ?? "", options.CommandName);
        var (screenshot, screenshotEvery, seconds) = (options.Screenshot, options.ScreenshotEvery, options.Seconds);
        var (serialPort, serialFile, serialStart, serialSpeed, serialLog) =
            (options.SerialPort, options.SerialFile, options.SerialStart, options.SerialSpeed, options.SerialLog);
        var (volumes, assigns) = (options.Volumes, options.Assigns);
        var (interpret, trace, stats, fastCpu) = (options.Interpret, options.Trace, options.Stats, options.FastCpu);
        var (scale, deinterlace, headless, audioFile) = (options.Scale, options.Deinterlace, options.Headless, options.AudioFile);
        var (streamPort, streamWide, streamName, streamAudio) =
            (options.StreamPort, options.StreamWide, options.StreamName, options.StreamAudio);
        var (genlock, genlockControl, genlockQueue) = (options.Genlock, options.GenlockControl, options.GenlockQueue);
        var (turboSeconds, turboLabel, watches, virtualTime) =
            (options.TurboSeconds, options.TurboLabel, options.Watches, options.VirtualTime);
        var (date, presses, copperDump) = (options.Date, options.Presses, options.CopperDump);

        var log = Console.Error;

        // A launcher that crashed or was killed could not remove its temporary folders.
        TempFolders.RemoveOld(log);

        // A program on a disk image: extract the disk, and run the program from the copy.
        string? diskCopy = null;
        byte[]? diskImage = null;
        var adfSeparator = executablePath.IndexOf(".adf:", StringComparison.OrdinalIgnoreCase);
        if (adfSeparator > 0)
        {
            var imagePath = executablePath[..(adfSeparator + 4)];
            AmigaSharp.Runtime.Dos.AdfImage disk;
            try
            {
                disk = AmigaSharp.Runtime.Dos.AdfImage.Read(File.ReadAllBytes(imagePath));
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Console.Error.WriteLine($"error: {imagePath}: {e.Message}");
                return 1;
            }

            diskCopy = TempFolders.Create("AmigaSharp-Disk-");
            disk.ExtractTo(diskCopy);
            log.WriteLine($"The disk {disk.VolumeName} is in {diskCopy}.");
            executablePath = Path.Combine([diskCopy, .. executablePath[(adfSeparator + 5)..].Split('/')]);
            drive ??= diskCopy;
            diskImage = File.ReadAllBytes(imagePath);
            volumes.Insert(0, ("DF0", diskCopy));
            if (disk.VolumeName.IndexOfAny([':', '/']) < 0)
                volumes.Insert(0, (disk.VolumeName, diskCopy));
        }

        var executable = File.ReadAllBytes(executablePath);
        drive ??= Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        commandName ??= Path.GetFileName(executablePath);

        var realTimeClock = virtualTime ? null : new RealTimeClock(start: false);
        IClock clock = realTimeClock ?? (IClock)new VirtualClock();
        var measuringClock = stats ? new MeasuringClock(clock) : null;
        clock = measuringClock ?? clock;
        var turbo = turboSeconds != null || turboLabel != null;
        var core = new Core(rootDirectory: drive, clock: clock) { TraceLibraryCalls = trace, PaceCpu = !fastCpu && !turbo };
        // A program from a disk image also finds the disk in DF0, for a program that reads the disk with the hardware.
        if (diskImage != null)
            core.Chipset.Disks.Drives[0].Insert(diskImage);
        if (date != null)
            core.SetDate(date.Value);
        foreach (var (name, path) in volumes)
            core.FileSystem.AddVolume(name, path);
        foreach (var (name, path) in assigns)
            core.FileSystem.AddAssign(name, path);

        // RAM: is a new directory for each run. T: is RAM:T, as in the Startup-Sequence of Workbench.
        var ram = TempFolders.Create("AmigaSharp-RAM-");
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

        // The extensions set up their parts, for example a line of the chipset, or requests for the port of the stream.
        using var context = new LauncherContext(core, clock, log, options, executable);
        foreach (var extension in app.Extensions)
            extension.Start(context);

        // The schedule needs the keys of the extensions, so the launcher reads it after they start.
        Schedule? schedule = null;
        var scheduleRequests = new ScheduleRequests();
        if (options.Schedule != null)
        {
            try
            {
                schedule = Schedule.Read(options.Schedule, context.ScheduleExtensions);
            }
            catch (FormatException e)
            {
                Console.Error.WriteLine($"error: {e.Message}");
                return 2;
            }

            context.StreamRequests.Add(scheduleRequests);
        }

        using var serialLogWriter = serialLog == null ? null : new StreamWriter(serialLog);
        using var loggingConnection = serialLogWriter == null
            ? null
            : new LoggingSerialConnection(core.Chipset.Custom.Serial.Connection, serialLogWriter,
                () => clock.Elapsed);
        if (loggingConnection != null)
            core.Chipset.Custom.Serial.Connection = loggingConnection;

        using var wavWriter = audioFile == null ? null : new WavWriter(audioFile, core.Chipset.Audio);

        core.Chipset.Display.Deinterlace = deinterlace;
        using var videoStream = streamPort is { } port
            ? new VideoStream(core.Chipset.Display, port, streamWide, streamName ?? commandName, log, streamAudio, genlock,
                genlockControl || schedule != null, core.Chipset.Audio.OpenTap(), context.StreamRequests,
                genlockQueue)
            : null;
        using var scheduleRunner = schedule != null && videoStream != null
            ? new ScheduleRunner(schedule, videoStream.Genlock!, videoStream.Mixer, context.ScheduleExtensions, log)
            : null;
        scheduleRequests.Runner = scheduleRunner;

        // A native (AOT) build cannot compile and load a translation while it runs, so it uses the interpreter. The check is
        // a constant in such a build, so the trimmer removes the compiler from it.
        TranslatedProgram program;
        if (!interpret && EmbeddedPrograms.TryGet(executable, out var createEmbedded))
        {
            log.WriteLine("Using the translation that is built into the launcher.");
            program = createEmbedded(core);
        }
        else if (!interpret && System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            program = CompileProgram(executable, listing, core, log);
        }
        else
        {
            if (!interpret)
                log.WriteLine("This build cannot compile a translation while it runs. The program runs in the interpreter.");
            program = new InterpretedProgram(core);
        }

        if (watches.Count > 0)
        {
            var symbols = AmigaSharp.Translator.VasmListing.Read(listing!).Symbols;
            var bases = AmigaSharp.Runtime.Loader.HunkLayout.Assign(AmigaSharp.Runtime.Loader.HunkFile.Parse(executable));
            foreach (var label in watches)
            {
                if (!symbols.TryGetValue(label, out var symbol) || symbol.Section is not { } section || section >= bases.Length)
                {
                    Console.Error.WriteLine($"error: the listing does not have the label {label}.");
                    return 2;
                }

                var address = bases[section] + symbol.Value;
                int? last = null;
                core.AddPollHandler(() =>
                {
                    var value = core.Memory.Read16(address);
                    if (value == last)
                        return;
                    log.WriteLine($"{clock.Elapsed.TotalSeconds,10:F3}  {label} = {value} (${value:X4}, {(short)value})");
                    last = value;
                });
            }
        }

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

        // The program runs on its own thread. The main thread shows the window, or waits for the screenshot.
        var finished = false;

        // SIGTERM and SIGHUP stop the launcher as Ctrl-C does, so that it closes the stream and ffmpeg.
        var terminated = false;
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                terminated = true;
            });
        using var hangUp = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGHUP, context =>
            {
                context.Cancel = true;
                terminated = true;
            });
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
                var lastInterpreted = 0L;
                var lastCycles = 0L;
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
                                  $"AUD1 {(delivered[InterruptBit.Audio0 + 1] - lastAudio1) / seconds:F0}, " +
                                  $"interpreted {(core.InterpretedInstructions - lastInterpreted) / seconds:F0} instructions/s, " +
                                  $"68000 at {(core.Cpu.Cycles - lastCycles) / seconds / 1e6:F2} MHz, " +
                                  $"free chip {core.Allocator.Available(MemoryFlags.Chip) / 1024} KB " +
                                  $"fast {core.Allocator.Available(MemoryFlags.Fast) / 1024} KB");
                    lastInterpreted = core.InterpretedInstructions;
                    lastCycles = core.Cpu.Cycles;
                    (lastVertb, lastAudio1) = (delivered[InterruptBit.VerticalBlank], delivered[InterruptBit.Audio0 + 1]);
                    (lastHost, lastAmiga, lastFrames, lastDropped, lastRender, lastWait) = (now, amiga, frames, dropped, render, wait);
                }
            }) { IsBackground = true, Name = "Stats" }.Start();
        }

        void WaitForTime(double time)
        {
            while (!finished && !terminated && clock.Elapsed.TotalSeconds < time)
                Thread.Sleep(5);
        }

        try
        {
            if (screenshot != null)
            {
                void Save(string path)
                {
                    var pixels = new uint[Display.Width * Display.Height];
                    core.Chipset.Display.CopyFrame(pixels);
                    File.WriteAllBytes(path, Png.Encode(Display.Width, Display.Height, pixels));
                    log.WriteLine($"Saved {path} (frame {core.Chipset.Display.FrameNumber}).");
                }

                if (screenshotEvery is { } every)
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(screenshot))!;
                    var name = Path.GetFileNameWithoutExtension(screenshot);
                    for (var time = every; time < seconds && !finished; time += every)
                    {
                        WaitForTime(time);
                        Save(Path.Combine(directory, $"{name}-{(int)time:D6}.png"));
                    }
                }

                WaitForTime(seconds);
                Save(screenshot);
            }
            else if (headless)
            {
                var stop = false;
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    stop = true;
                };
                while (!finished && !stop && !terminated)
                    Thread.Sleep(100);
            }
            else
            {
                void Key(Silk.NET.SDL.Scancode scancode, bool up)
                {
                    if (KeyboardMapping.TryGetRawKey(scancode, out var rawKey))
                        core.KeyboardInput.PostRawKey(rawKey, up);
                }

                var ports = core.Chipset.Custom.Ports;
                new DisplayWindow(core.Chipset.Display, $"AmigaSharp: {commandName}", scale, Key, ports[0], ports[1],
                        wavWriter == null ? core.Chipset.Audio : null)
                    .Run(() => finished || terminated);
            }
        }
        finally
        {
            TempFolders.Delete(ram);
            if (diskCopy != null)
                TempFolders.Delete(diskCopy);
        }

        if (stats && core.InterpreterEntries.Count > 0)
        {
            log.WriteLine($"The interpreter ran {core.InterpretedInstructions} instructions. The most frequent entries:");
            foreach (var (address, count) in core.InterpreterEntries.OrderByDescending(e => e.Value).Take(10).ToList())
                log.WriteLine($"  ${address:X6}: {count} times");
        }

        return exitCode;
    }

    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Compiles and loads the translation of the program.")]
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Loads the translation of the program as an assembly.")]
    private static TranslatedProgram CompileProgram(byte[] executable, string? listing, Core core, TextWriter log) =>
        (TranslatedProgram)Activator.CreateInstance(ProgramCompiler.Compile(executable, listing, log), core)!;

    private static int Extract(string[] arguments)
    {
        if (arguments is not [var image, "--output", var output])
        {
            Console.Error.WriteLine("error: extract needs a disk image and --output <directory>.");
            return 2;
        }

        try
        {
            var disk = AmigaSharp.Runtime.Dos.AdfImage.Read(File.ReadAllBytes(image));
            disk.ExtractTo(output);
            Console.WriteLine($"{image}: the disk {disk.VolumeName} ({(disk.FastFileSystem ? "FFS" : "OFS")}).");
            foreach (var entry in disk.Entries)
                Console.WriteLine(entry.IsDirectory ? $"  {entry.Path}/" : $"  {entry.Path} ({entry.Data.Length} bytes)");
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"error: {image}: {e.Message}");
            return 1;
        }
    }

    private static int Unpack(string[] arguments)
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
}
