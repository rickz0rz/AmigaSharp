using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Graphics;
using AmigaSharp.Runtime.Hardware;
using AmigaSharp.Translator;

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
    /// <summary>The exit code when the watchdog stopped the program.</summary>
    public const int WatchdogExitCode = 3;

    private const string UsageTemplate = """
        Usage: @NAME@ <executable> [options]

        @DESCRIPTION@

        Usage: @NAME@ unpack <file>... --output <directory>

        Unpacks PowerPacker files (files that start with PP20) to the directory.

        Usage: @NAME@ extract <disk.adf> --output <directory>

        Writes the files of an ADF disk image to the directory.

        Usage: @NAME@ merge-code-maps <output> <map>...

        Writes all the addresses of the maps of the code that ran (see --code-map) to one map. For example, merge the
        maps of the runs on other computers, and give the result to --code-map or to the translator.

        The executable can be a file on a disk image: <disk.adf>:<path>, for example AmigaTestKit.adf:AmigaTestKit. The
        launcher copies the files of the disk to a new temporary directory. That directory is SYS:, DF0: and the volume
        name of the disk. The program can change these files, but not the disk image. The disk is also in the drive DF0
        for disk DMA. A write there changes only a copy in memory.

        Options:
          --listing <file.lst>      The vasm listing of the executable. The translator uses its instructions and labels.
          --interpret               Run the program in the interpreter. Do not translate it. A native (AOT) build always
                                    uses the interpreter.
          --no-code-map             Without --listing, the launcher keeps a map of the code that ran in the interpreter,
                                    and the next translation also translates that code. So each run translates more of
                                    the program. This option does not read or write the map.
          --code-map <file>         Read and update this map, in place of the map of the program in the cache of the
                                    translations. The map can then go with the program to another computer.
          --drive <directory>       The host directory of SYS:. The default is the directory of the executable.
          --volume <NAME>=<dir>     A volume on a host directory, for example DH1=/path/to/drive.
          --disk <DFn>=<file.adf>   Put a disk image in a drive (DF0 to DF3), for example DF1=data.adf. The program
                                    finds its files in the volume DFn: and in the volume with the name of the disk, and
                                    the drive also gives the disk to a program that reads it with the hardware. The
                                    option can occur more than once.
          --assign <NAME>=<path>    An assign to an AmigaDOS directory, for example DF0=DH1: or FONTS=SYS:fonts.
          --arguments <text>        The command line arguments of the program.
          --command-name <name>     The name of the command. The default is the name of the executable.
          --pal                     Make a PAL Amiga: 312 lines and about 50 frames each second, with the clocks of PAL.
                                    The picture is then 768 by 572 pixels. The default is NTSC.
          --chipset <name>          ecs (the default, as an A2000) or aga (Alice and Lisa, as an A1200). With aga, the
                                    chip IDs and graphics.library tell a program that the chipset is AGA, and the
                                    display has the fetch modes, 8 planes, the palette of 256 colors, and sprites of 32
                                    and 64 pixels. See the README for the parts that it does not have.
          --unaligned-access        Let the program read and write words and longs at odd addresses, as a 68020 does,
                                    for example a program for the A1200. On a 68000, this is an address error.
          --cpu-mhz <n>             Run the CPU at n MHz of 68000 cycles. The default is 7.16, the speed of the 68000. A
                                    68020 needs fewer cycles for an instruction: for a program of the A1200 (68020 at
                                    14 MHz), use for example 56.
          --serial-port <port>      The TCP port of the serial bridge. The default is 5400. 0 turns the bridge off.
          --serial-file <file>      Replay a captured feed on the serial port, in place of the bridge. The replay starts
                                    when the program enables the RBF interrupt, and it goes at the baud rate of SERPER.
          --serial-start <seconds>  The time of the Amiga clock before the replay can start. The default is 0. ESQ empties
                                    its receive buffer while it starts, so give it time: for example 8.
          --serial-speed <n>        Receive the serial bytes n times faster than the baud rate of SERPER. The default is 1.
                                    ESQ has no flow control: a factor that is too large fills its receive buffer.
          --serial-log <file>       Write each serial byte in the two directions to the file, with the time.
          --scale <n>               The size of the window: 1 is 768 by 480 pixels (572 with --pal). The default is 1.
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
          --watchdog <seconds>      Stop the program when its picture does not change for the seconds, with the exit
                                    code 3. Use it for a program whose picture always moves, for example Prevue, so
                                    that a script can start it again.
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
        if (args.Length > 0 && args[0] == "merge-code-maps")
            return MergeCodeMaps(args[1..]);

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

        var log = Console.Error;

        // A launcher that crashed or was killed could not remove its temporary folders.
        TempFolders.RemoveOld(log);

        // The temporary folders of this run: RAM: and the copies of the disks. They go when the run ends, also after an
        // error.
        var temporary = new List<string>();
        try
        {
            return RunProgram(options, app, log, temporary);
        }
        finally
        {
            foreach (var folder in temporary)
                TempFolders.Delete(folder);
        }
    }

    /// <summary>The state that the threads of a run share.</summary>
    private sealed class RunState
    {
        /// <summary>True when the program ended.</summary>
        public volatile bool Finished;

        /// <summary>True when a signal or the watchdog stops the launcher.</summary>
        public volatile bool Terminated;

        public int ExitCode;
    }

    /// <summary>The disks of a run, and the path of the program on them.</summary>
    private sealed record Disks(
        string ExecutablePath,
        string? Drive,
        List<(string Name, string Path)> Volumes,
        List<(int Drive, byte[] Image)> Images);

    /// <summary>The screenshots of a run: their times and files, and the pictures that the 68000 thread copied.</summary>
    private sealed record Screenshots(
        List<(double Seconds, string Path)> Paths,
        System.Collections.Concurrent.BlockingCollection<(string Path, uint[] Pixels, long Frame)> Pictures);

    private static int RunProgram(LauncherOptions options, LauncherApp app, TextWriter log, List<string> temporary)
    {
        if (MountDisks(options, log, temporary) is not { } disks)
            return 1;
        var executable = File.ReadAllBytes(disks.ExecutablePath);
        var drive = disks.Drive ?? Path.GetDirectoryName(Path.GetFullPath(disks.ExecutablePath))!;
        var commandName = options.CommandName ?? Path.GetFileName(disks.ExecutablePath);

        var realTimeClock = options.VirtualTime ? null : new RealTimeClock(start: false);
        IClock clock = realTimeClock ?? (IClock)new VirtualClock();
        var measuringClock = options.Stats ? new MeasuringClock(clock) : null;
        clock = measuringClock ?? clock;

        var core = CreateCore(options, drive, clock, disks);
        SetUpFileSystem(core, options, disks, temporary);
        using var bridge = SetUpSerialInput(core, options, clock, log);

        // The extensions set up their parts, for example a line of the chipset, or requests for the port of the stream.
        using var context = new LauncherContext(core, clock, log, options, executable);
        foreach (var extension in app.Extensions)
            extension.Start(context);

        // The schedule needs the keys of the extensions, so the launcher reads it after they start.
        var scheduleRequests = new ScheduleRequests();
        Schedule? schedule = null;
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

        using var serialLogWriter = options.SerialLog == null ? null : new StreamWriter(options.SerialLog);
        using var loggingConnection = serialLogWriter == null
            ? null
            : new LoggingSerialConnection(core.Chipset.Custom.Serial.Connection, serialLogWriter, () => clock.Elapsed);
        if (loggingConnection != null)
            core.Chipset.Custom.Serial.Connection = loggingConnection;

        using var wavWriter = options.AudioFile == null ? null : new WavWriter(options.AudioFile, core.Chipset.Audio);

        core.Chipset.Display.Deinterlace = options.Deinterlace;
        using var videoStream = options.StreamPort is { } port
            ? new VideoStream(core.Chipset.Display, port, options.StreamWide, options.StreamName ?? commandName, log,
                options.StreamAudio, options.Genlock, options.GenlockControl || schedule != null,
                core.Chipset.Audio.OpenTap(), context.StreamRequests, options.GenlockQueue)
            : null;
        using var scheduleRunner = schedule != null && videoStream != null
            ? new ScheduleRunner(schedule, videoStream.Genlock!, videoStream.Mixer, context.ScheduleExtensions, log)
            : null;
        scheduleRequests.Runner = scheduleRunner;

        // Without a listing, the map of the code that ran in earlier runs tells the translator where more code is.
        var codeMap = options.Listing == null && options.CodeMap
            ? options.CodeMapFile != null ? CodeMap.Open(options.CodeMapFile) : CodeMap.Load(executable)
            : null;
        core.RecordJumpTargets = codeMap != null;

        var program = ChooseProgram(options, executable, core, log, codeMap);
        if (!AddWatches(options, executable, core, clock, log) || !AddTurbo(options, executable, core, clock, log))
            return 2;

        var state = new RunState();
        // SIGTERM and SIGHUP stop the launcher as Ctrl-C does, so that it closes the stream and ffmpeg.
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                state.Terminated = true;
            });
        using var hangUp = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGHUP, signal =>
            {
                signal.Cancel = true;
                state.Terminated = true;
            });

        StartProgram(program, executable, options.Arguments ?? "", commandName, core, realTimeClock, state, log);

        // The watchdog stops the program when its picture does not change, with the exit code 3.
        using var watchdog = options.Watchdog is { } watchdogSeconds
            ? new Watchdog(core.Chipset.Display.CopyFrame, TimeSpan.FromSeconds(watchdogSeconds), log, () =>
            {
                state.ExitCode = WatchdogExitCode;
                state.Terminated = true;
            }, height: core.Chipset.Display.Height)
            : null;

        var screenshots = ScheduleTimedActions(options, core);
        if (measuringClock != null)
            StartStats(core, clock, measuringClock, state, log);

        WaitForEnd(options, core, state, screenshots, commandName, wavWriter != null, log);
        Finish(core, codeMap, executable, options.Stats, log);
        return state.ExitCode;
    }

    /// <summary>
    /// Copies the files of the disks to temporary folders: the disk of the program in DF0, and the disks of --disk. Each
    /// disk is the volume DFn and the volume with the name of the disk. Returns null after an error.
    /// </summary>
    private static Disks? MountDisks(LauncherOptions options, TextWriter log, List<string> temporary)
    {
        var executablePath = options.ExecutablePath!;
        var drive = options.Drive;
        var volumes = new List<(string Name, string Path)>();
        var images = new List<(int Drive, byte[] Image)>();
        var disks = new List<(int Drive, string Path)>(options.Disks);
        var adfSeparator = executablePath.IndexOf(".adf:", StringComparison.OrdinalIgnoreCase);
        if (adfSeparator > 0)
        {
            if (disks.Any(disk => disk.Drive == 0))
            {
                Console.Error.WriteLine("error: the program is on a disk image in DF0, so --disk cannot use DF0.");
                return null;
            }

            disks.Insert(0, (0, executablePath[..(adfSeparator + 4)]));
        }

        foreach (var (number, imagePath) in disks)
        {
            AmigaSharp.Runtime.Dos.AdfImage disk;
            byte[] image;
            try
            {
                image = File.ReadAllBytes(imagePath);
                disk = AmigaSharp.Runtime.Dos.AdfImage.Read(image);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Console.Error.WriteLine($"error: {imagePath}: {e.Message}");
                return null;
            }

            var diskCopy = TempFolders.Create("AmigaSharp-Disk-");
            temporary.Add(diskCopy);
            disk.ExtractTo(diskCopy);
            log.WriteLine($"The disk {disk.VolumeName} is in DF{number}, and its files are in {diskCopy}.");
            foreach (var (path, reason) in disk.DamagedFiles)
                log.WriteLine($"warning: the disk {disk.VolumeName} cannot give the file {path}. {reason}");
            images.Add((number, image));
            volumes.Insert(0, ($"DF{number}", diskCopy));
            if (disk.VolumeName.IndexOfAny([':', '/']) < 0)
                volumes.Insert(0, (disk.VolumeName, diskCopy));
            if (number == 0 && adfSeparator > 0)
            {
                executablePath = Path.Combine([diskCopy, .. executablePath[(adfSeparator + 5)..].Split('/')]);
                drive ??= diskCopy;
            }
        }

        // The volumes of the options come after the volumes of the disks, so they can replace them.
        volumes.AddRange(options.Volumes);
        return new Disks(executablePath, drive, volumes, images);
    }

    /// <summary>Makes the Amiga of the options, with the disks in its drives.</summary>
    private static Core CreateCore(LauncherOptions options, string drive, IClock clock, Disks disks)
    {
        var turbo = options.TurboSeconds != null || options.TurboLabel != null;
        var core = new Core(rootDirectory: drive, clock: clock, machine: options.Machine)
        {
            TraceLibraryCalls = options.Trace,
            PaceCpu = !options.FastCpu && !turbo,
        };
        // The drives also have the disks, for a program that reads a disk with the hardware. DF1 to DF3 are not connected
        // without a disk.
        foreach (var (number, image) in disks.Images)
        {
            if (!core.Chipset.Disks.Drives[number].Present)
                core.Chipset.Disks.Drives[number] = new FloppyDrive(present: true);
            core.Chipset.Disks.Drives[number].Insert(image);
        }

        if (options.Date != null)
            core.SetDate(options.Date.Value);
        return core;
    }

    /// <summary>Makes PROGDIR:, the volumes, the assigns, RAM: and T:.</summary>
    private static void SetUpFileSystem(Core core, LauncherOptions options, Disks disks, List<string> temporary)
    {
        // PROGDIR: is the directory of the program, as in AmigaDOS 2.0 and later. An option can replace it.
        core.FileSystem.AddVolume("PROGDIR", Path.GetDirectoryName(Path.GetFullPath(disks.ExecutablePath))!);
        foreach (var (name, path) in disks.Volumes)
            core.FileSystem.AddVolume(name, path);
        foreach (var (name, path) in options.Assigns)
            core.FileSystem.AddAssign(name, path);

        // RAM: is a new directory for each run. T: is RAM:T, as in the Startup-Sequence of Workbench.
        var ram = TempFolders.Create("AmigaSharp-RAM-");
        temporary.Add(ram);
        Directory.CreateDirectory(Path.Combine(ram, "T"));
        core.FileSystem.AddVolume("RAM", ram);
        if (options.Assigns.All(assign => !assign.Name.Equals("T", StringComparison.OrdinalIgnoreCase)))
            core.FileSystem.AddAssign("T", "RAM:T");
    }

    /// <summary>
    /// Connects the serial port: the TCP bridge, or the replay of a file. Sets the speed factor. Returns the bridge.
    /// </summary>
    private static TcpSerialBridge? SetUpSerialInput(Core core, LauncherOptions options, IClock clock, TextWriter log)
    {
        var serial = core.Chipset.Custom.Serial;
        var bridge = options.SerialPort > 0 && options.SerialFile == null
            ? new TcpSerialBridge(options.SerialPort, log: log)
            : null;
        if (bridge != null)
        {
            serial.Connection = bridge;
            log.WriteLine($"Serial bridge on localhost:{bridge.Port}. For example: nc localhost {bridge.Port}");
        }

        if (options.SerialFile is { } serialFile)
        {
            var feed = File.ReadAllBytes(serialFile);
            var custom = core.Chipset.Custom;
            var start = options.SerialStart;
            serial.Connection = new ReplaySerialConnection(feed,
                () => clock.Elapsed.TotalSeconds >= start && (custom.Intena & (1 << InterruptBit.Rbf)) != 0);
            log.WriteLine($"Replaying {feed.Length} bytes from {serialFile} on the serial port.");
        }

        serial.SpeedFactor = options.SerialSpeed;
        return bridge;
    }

    /// <summary>
    /// The program to run: the translation in the launcher, a translation that the launcher compiles now, or the
    /// interpreter. A native (AOT) build cannot compile and load a translation while it runs, so it uses the
    /// interpreter. The check is a constant in such a build, so the trimmer removes the compiler from it.
    /// </summary>
    private static TranslatedProgram ChooseProgram(LauncherOptions options, byte[] executable, Core core, TextWriter log,
        CodeMap? codeMap)
    {
        if (!options.Interpret && EmbeddedPrograms.TryGet(executable, out var createEmbedded))
        {
            log.WriteLine("Using the translation that is built into the launcher.");
            return createEmbedded(core);
        }

        if (!options.Interpret && System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return CompileProgram(executable, options.Listing, core, log, codeMap?.Addresses);

        if (!options.Interpret)
            log.WriteLine("This build cannot compile a translation while it runs. The program runs in the interpreter.");
        return new InterpretedProgram(core);
    }

    /// <summary>The address of a label of the listing in the loaded program, or null if the listing does not have it.</summary>
    private static uint? LabelAddress(LauncherOptions options, byte[] executable, string label)
    {
        var symbols = VasmListing.Read(options.Listing!).Symbols;
        var bases = AmigaSharp.Runtime.Loader.HunkLayout.Assign(AmigaSharp.Runtime.Loader.HunkFile.Parse(executable));
        if (!symbols.TryGetValue(label, out var symbol) || symbol.Section is not { } section || section >= bases.Length)
        {
            Console.Error.WriteLine($"error: the listing does not have the label {label}.");
            return null;
        }

        return bases[section] + symbol.Value;
    }

    /// <summary>Writes each change of the words of --watch. Returns false if the listing does not have a label.</summary>
    private static bool AddWatches(LauncherOptions options, byte[] executable, Core core, IClock clock, TextWriter log)
    {
        foreach (var label in options.Watches)
        {
            if (LabelAddress(options, executable, label) is not { } address)
                return false;
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

        return true;
    }

    /// <summary>
    /// Ends the turbo after --turbo seconds, or when the word at the label of --turbo-until is not 0. The launcher
    /// checks at each safe point. Returns false if the listing does not have the label.
    /// </summary>
    private static bool AddTurbo(LauncherOptions options, byte[] executable, Core core, IClock clock, TextWriter log)
    {
        var (turboSeconds, turboLabel) = (options.TurboSeconds, options.TurboLabel);
        if ((turboSeconds == null && turboLabel == null) || options.FastCpu)
            return true;

        uint? turboAddress = null;
        if (turboLabel != null && (turboAddress = LabelAddress(options, executable, turboLabel)) == null)
            return false;

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
        return true;
    }

    /// <summary>Runs the program on its own thread. The main thread shows the window, or waits for the screenshots.</summary>
    private static void StartProgram(TranslatedProgram program, byte[] executable, string arguments, string commandName,
        Core core, RealTimeClock? realTimeClock, RunState state, TextWriter log)
    {
        new Thread(() =>
        {
            try
            {
                realTimeClock?.Start();
                var result = program.Run(executable, arguments, commandName);
                log.WriteLine($"The program returned {(int)result}.");
                state.ExitCode = (int)result;
            }
            catch (Exception e)
            {
                log.WriteLine($"The program stopped: {e.GetType().Name}: {e.Message}");
                log.WriteLine($"PC ${core.Cpu.Pc:X6}, A7 ${core.Cpu.A[7]:X6}");
                state.ExitCode = 1;
            }
            finally
            {
                state.Finished = true;
            }
        }, 64 * 1024 * 1024) { IsBackground = true, Name = "68000" }.Start();
    }

    /// <summary>
    /// Adds the key presses, the copper dump and the screenshots at their Amiga time. They happen on the 68000 thread,
    /// so with --virtual-time they are at the same point of the program in each run. Returns the screenshots.
    /// </summary>
    private static Screenshots ScheduleTimedActions(LauncherOptions options, Core core)
    {
        var timed = new TimedActions(core);

        // The scripted key presses: each key goes down, and up again 0.1 second later.
        foreach (var (time, rawKey) in options.Presses)
        {
            timed.Add(time, () => core.KeyboardInput.PostRawKey(rawKey, up: false));
            timed.Add(time + 0.1, () => core.KeyboardInput.PostRawKey(rawKey, up: true));
        }

        if (options.CopperDump is var (dumpTime, dumpPath))
        {
            timed.Add(dumpTime, () =>
            {
                core.Chipset.Display.CopperDump = new StreamWriter(dumpPath);
                File.WriteAllBytes(dumpPath + ".chip", core.Memory.Ram(0, 0x20_0000).ToArray());
            });
        }

        // The 68000 thread copies the picture of each screenshot, and the main thread writes the files.
        var screenshots = new Screenshots([], new());
        if (options.Screenshot is not { } screenshot)
            return screenshots;
        if (options.ScreenshotEvery is { } every)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(screenshot))!;
            var name = Path.GetFileNameWithoutExtension(screenshot);
            for (var time = every; time < options.Seconds; time += every)
                screenshots.Paths.Add((time, Path.Combine(directory, $"{name}-{(int)time:D6}.png")));
        }

        screenshots.Paths.Add((options.Seconds, screenshot));
        foreach (var (time, path) in screenshots.Paths)
            timed.Add(time, () => screenshots.Pictures.Add((path, CopyPicture(core), core.Chipset.Display.FrameNumber)));
        return screenshots;
    }

    private static uint[] CopyPicture(Core core)
    {
        var pixels = new uint[Display.Width * core.Chipset.Display.Height];
        core.Chipset.Display.CopyFrame(pixels);
        return pixels;
    }

    /// <summary>Writes the speed of the emulation each second, for --stats.</summary>
    private static void StartStats(Core core, IClock clock, MeasuringClock measuringClock, RunState state, TextWriter log)
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
            while (!state.Finished)
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

    /// <summary>
    /// Waits for the end of the run on the main thread: writes the screenshots, or waits without a window, or shows the
    /// window until the program ends or the launcher stops.
    /// </summary>
    private static void WaitForEnd(LauncherOptions options, Core core, RunState state, Screenshots screenshots,
        string commandName, bool soundToFile, TextWriter log)
    {
        if (options.Screenshot is { } screenshot)
        {
            void Save(string path, uint[] pixels, long frame)
            {
                File.WriteAllBytes(path, Png.Encode(Display.Width, core.Chipset.Display.Height, pixels));
                log.WriteLine($"Saved {path} (frame {frame}).");
            }

            // Write the screenshots as the 68000 thread makes them. If the program stops first, the last screenshot
            // shows its last picture.
            var saved = 0;
            while (saved < screenshots.Paths.Count)
            {
                if (screenshots.Pictures.TryTake(out var shot, 100))
                {
                    Save(shot.Path, shot.Pixels, shot.Frame);
                    saved++;
                }
                else if (state.Finished || state.Terminated)
                {
                    while (screenshots.Pictures.TryTake(out shot))
                        Save(shot.Path, shot.Pixels, shot.Frame);
                    Save(screenshot, CopyPicture(core), core.Chipset.Display.FrameNumber);
                    break;
                }
            }
        }
        else if (options.Headless)
        {
            var stop = false;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop = true;
            };
            while (!state.Finished && !stop && !state.Terminated)
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
            new DisplayWindow(core.Chipset.Display, $"AmigaSharp: {commandName}", options.Scale, Key, ports[0], ports[1],
                    soundToFile ? null : core.Chipset.Audio)
                .Run(() => state.Finished || state.Terminated);
        }
    }

    /// <summary>Adds the code that ran in the interpreter to the map, and writes the entries of the interpreter for --stats.</summary>
    private static void Finish(Core core, CodeMap? codeMap, byte[] executable, bool stats, TextWriter log)
    {
        if (codeMap != null)
        {
            var added = codeMap.Add(executable, core.Memory,
                core.InterpreterEntries.Keys.Concat(core.InterpretedJumpTargets).ToList());
            if (added > 0)
                log.WriteLine($"The map of the code that ran has {codeMap.Addresses.Count} addresses ({added} new). " +
                              $"The next start also translates them. The map is {codeMap.Path}.");
        }

        if (stats && core.InterpreterEntries.Count > 0)
        {
            log.WriteLine($"The interpreter ran {core.InterpretedInstructions} instructions. The most frequent entries:");
            foreach (var (address, count) in core.InterpreterEntries.OrderByDescending(e => e.Value).Take(10).ToList())
                log.WriteLine($"  ${address:X6}: {count} times");
        }
    }

    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Compiles and loads the translation of the program.")]
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Loads the translation of the program as an assembly.")]
    private static TranslatedProgram CompileProgram(byte[] executable, string? listing, Core core, TextWriter log,
        IReadOnlyCollection<uint>? knownCode) =>
        (TranslatedProgram)Activator.CreateInstance(ProgramCompiler.Compile(executable, listing, log, knownCode), core)!;

    private static int MergeCodeMaps(string[] arguments)
    {
        if (arguments.Length < 2)
        {
            Console.Error.WriteLine("error: merge-code-maps needs an output file and one or more maps.");
            return 2;
        }

        try
        {
            var count = KnownCodeFile.Merge(arguments[0], arguments[1..]);
            Console.WriteLine($"Wrote {count} addresses to {arguments[0]}.");
            return 0;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

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
            foreach (var (path, reason) in disk.DamagedFiles)
                Console.Error.WriteLine($"warning: the disk cannot give the file {path}. {reason}");
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
