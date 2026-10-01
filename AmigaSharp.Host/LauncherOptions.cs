using System.Globalization;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Host;

/// <summary>The options of a launcher, from its command line. An extension can read more options and set defaults.</summary>
public sealed class LauncherOptions
{
    public string? ExecutablePath { get; set; }
    public string? Listing { get; set; }
    public bool Interpret { get; set; }
    public string? Drive { get; set; }
    public List<(string Name, string Path)> Volumes { get; } = [];
    public List<(string Name, string Path)> Assigns { get; } = [];

    /// <summary>The command line arguments of the program. Null if the command line does not give them.</summary>
    public string? Arguments { get; set; }

    public string? CommandName { get; set; }
    public int SerialPort { get; set; } = 5400;
    public string? SerialFile { get; set; }
    public double SerialStart { get; set; }
    public double SerialSpeed { get; set; } = 1;
    public string? SerialLog { get; set; }
    public int Scale { get; set; } = 1;
    public DeinterlaceMode Deinterlace { get; set; } = DeinterlaceMode.Weave;
    public bool Headless { get; set; }
    public int? StreamPort { get; set; }
    public bool StreamWide { get; set; } = true;
    public string? StreamName { get; set; }
    public string? StreamAudio { get; set; }
    public string? Genlock { get; set; }
    public bool GenlockControl { get; set; }
    public QueueRequest.QueueFile? GenlockQueue { get; set; }

    /// <summary>A schedule file for the genlock queue and the extensions (see <see cref="Host.Schedule"/>).</summary>
    public string? Schedule { get; set; }
    public string? AudioFile { get; set; }
    public string? Screenshot { get; set; }
    public double Seconds { get; set; } = 10;
    public double? ScreenshotEvery { get; set; }
    public DateTime? Date { get; set; }
    public bool VirtualTime { get; set; }

    /// <summary>True for a PAL Amiga. The default is NTSC.</summary>
    public bool Pal { get; set; }

    /// <summary>True for the AGA chipset. The default is ECS.</summary>
    public bool Aga { get; set; }

    /// <summary>True to allow words and longs at odd addresses, as a 68020 does.</summary>
    public bool UnalignedAccess { get; set; }

    /// <summary>The speed of the CPU in MHz, or null for the 7.16 MHz of the 68000.</summary>
    public double? CpuMhz { get; set; }

    /// <summary>False to not read or write the map of the code that ran (see <see cref="CodeMap"/>).</summary>
    public bool CodeMap { get; set; } = true;
    public List<(double Seconds, byte RawKey)> Presses { get; } = [];
    public (double Seconds, string Path)? CopperDump { get; set; }
    public bool FastCpu { get; set; }
    public double? TurboSeconds { get; set; }
    public string? TurboLabel { get; set; }
    public bool Stats { get; set; }

    /// <summary>The seconds that the picture can stay the same before the launcher stops the program, or null.</summary>
    public double? Watchdog { get; set; }
    public List<string> Watches { get; } = [];
    public bool Trace { get; set; }

    /// <summary>True for --help: the launcher shows its usage and stops.</summary>
    public bool Help { get; private set; }

    /// <summary>Reads the command line. An option that the launcher does not know goes to the extensions.</summary>
    /// <exception cref="ArgumentException">An option is not correct.</exception>
    /// <exception cref="FormatException">A value is not correct.</exception>
    public static LauncherOptions Parse(string[] args, IReadOnlyList<ILauncherExtension> extensions)
    {
        var options = new LauncherOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            (string, string) Pair()
            {
                var value = Next();
                var equals = value.IndexOf('=');
                return equals > 0 ? (value[..equals], value[(equals + 1)..]) : throw new ArgumentException($"{value} is not NAME=value.");
            }

            double Number() => double.Parse(Next(), CultureInfo.InvariantCulture);

            switch (args[i])
            {
                case "--listing": options.Listing = Next(); break;
                case "--audio-file": options.AudioFile = Next(); break;
                case "--interpret": options.Interpret = true; break;
                case "--drive": options.Drive = Next(); break;
                case "--volume": options.Volumes.Add(Pair()); break;
                case "--assign": options.Assigns.Add(Pair()); break;
                case "--arguments": options.Arguments = Next(); break;
                case "--command-name": options.CommandName = Next(); break;
                case "--serial-port": options.SerialPort = int.Parse(Next()); break;
                case "--serial-file": options.SerialFile = Next(); break;
                case "--serial-start": options.SerialStart = Number(); break;
                case "--serial-log": options.SerialLog = Next(); break;
                case "--serial-speed": options.SerialSpeed = Number(); break;
                case "--scale": options.Scale = int.Parse(Next()); break;
                case "--screenshot": options.Screenshot = Next(); break;
                case "--seconds": options.Seconds = Number(); break;
                case "--screenshot-every": options.ScreenshotEvery = Number(); break;
                case "--trace": options.Trace = true; break;
                case "--stats": options.Stats = true; break;
                case "--watchdog":
                    options.Watchdog = Number();
                    if (options.Watchdog < 5)
                        throw new ArgumentException("--watchdog needs 5 seconds or more.");
                    break;
                case "--deinterlace": options.Deinterlace = Enum.Parse<DeinterlaceMode>(Next(), ignoreCase: true); break;
                case "--headless": options.Headless = true; break;
                case "--stream": options.StreamPort = int.Parse(Next()); break;
                case "--stream-4x3": options.StreamWide = false; break;
                case "--stream-name": options.StreamName = Next(); break;
                case "--stream-audio": options.StreamAudio = Next(); break;
                case "--genlock": options.Genlock = Next(); break;
                case "--genlock-control": options.GenlockControl = true; break;
                case "--genlock-playlist": options.GenlockQueue = QueueRequest.ReadFile(Next()); break;
                case "--schedule": options.Schedule = Next(); break;
                case "--fast-cpu": options.FastCpu = true; break;
                case "--turbo": options.TurboSeconds = Number(); break;
                case "--turbo-until": options.TurboLabel = Next(); break;
                case "--watch": options.Watches.Add(Next()); break;
                case "--virtual-time": options.VirtualTime = true; break;
                case "--pal": options.Pal = true; break;
                case "--unaligned-access": options.UnalignedAccess = true; break;
                case "--no-code-map": options.CodeMap = false; break;
                case "--cpu-mhz":
                    options.CpuMhz = double.Parse(Next(), CultureInfo.InvariantCulture);
                    if (options.CpuMhz is < 1 or > 1000)
                        throw new ArgumentException("--cpu-mhz needs a speed from 1 to 1000.");
                    break;
                case "--chipset":
                    options.Aga = Next().ToLowerInvariant() switch
                    {
                        "ecs" => false,
                        "aga" => true,
                        var other => throw new ArgumentException($"--chipset is ecs or aga, not {other}."),
                    };
                    break;
                case "--date": options.Date = DateTime.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--copper-dump":
                {
                    var (time, path) = Pair();
                    options.CopperDump = (double.Parse(time, CultureInfo.InvariantCulture), path);
                    break;
                }
                case "--press":
                {
                    var (time, key) = Pair();
                    options.Presses.Add((double.Parse(time, CultureInfo.InvariantCulture),
                        KeyNames.RawKey(key) ?? throw new ArgumentException($"{key} is not a key name.")));
                    break;
                }
                case "--help" or "-h":
                    options.Help = true;
                    return options;
                case var value when !value.StartsWith("--") && options.ExecutablePath == null:
                    options.ExecutablePath = value;
                    break;
                default:
                    var option = args[i];
                    if (!extensions.Any(extension => extension.TryParse(option, Next)))
                        throw new ArgumentException($"unknown argument {option}.");
                    break;
            }
        }

        return options;
    }

    /// <summary>Checks the options together, after the extensions set their defaults.</summary>
    /// <exception cref="ArgumentException">The options are not correct.</exception>
    public void Check()
    {
        if (ExecutablePath == null)
            throw new ArgumentException("the executable is missing.");
        if ((Genlock != null || GenlockControl || GenlockQueue != null || Schedule != null) && StreamPort == null)
            throw new ArgumentException("--genlock, --genlock-control, --genlock-playlist and --schedule need --stream.");
        if (Schedule != null && (Genlock != null || GenlockQueue != null))
            throw new ArgumentException("--schedule fills the genlock queue. Do not use it with --genlock or --genlock-playlist.");
        if ((TurboLabel != null || Watches.Count > 0) && Listing == null)
            throw new ArgumentException("--turbo-until and --watch need --listing.");
    }
}
