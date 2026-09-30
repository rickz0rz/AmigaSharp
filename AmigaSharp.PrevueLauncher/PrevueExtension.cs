using AmigaSharp.Host;
using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.PrevueLauncher;

/// <summary>
/// The parts of the launcher for Prevue Guide (ESQ): its control line, its requests on the port of the stream
/// (/prevue/ctrl), the trace of its feed parser, and the defaults of a Prevue machine.
/// </summary>
public sealed class PrevueExtension : ILauncherExtension
{
    /// <summary>The selection code of the machine when the command line does not give one.</summary>
    public const string DefaultCode = "GA24005";

    /// <summary>A word of ESQ that becomes 1 when its main loop starts. The turbo ends there.</summary>
    public const string MainLoopLabel = "_ESQ_MainLoopUiTickEnabledFlag";

    private int _ctrlPort;
    private string? _ctrlFile;
    private string? _feedTrace;

    public string Usage => """
        Options for Prevue:
          --prevue-ctrl-port <port> A TCP port for the 110 baud control line (CTRL) of Prevue, on the CTS pin of the
                                    serial port. Each byte that a client sends goes on the line. The line keeps the
                                    bytes until one second after Prevue starts to sample it. See docs/ctrl-line.md.
          --prevue-ctrl-file <file> Send the bytes of the file on the control line, in place of --prevue-ctrl-port.
                                    The bytes go one second after Prevue starts to sample the line (after it enables
                                    the AUD1 interrupt).
          --prevue-feed-trace <file>
                                    Write the commands that the feed parser of ESQ reads, and the changes of its
                                    counters, to the file. This option needs --listing.

        With --stream, the port of the stream also has the requests of /prevue/ctrl: promos and logos in the top half
        of the screen. See docs/orchestration.md.

        Defaults of a Prevue machine: with --drive, the drive is also DH1:, and DF0: and ENV: are DH1:. The executable
        is ESQ on the drive, the command name is esq, and the arguments are the selection code GA24005. The turbo
        runs until the main loop of ESQ starts (with --listing), or for 8 seconds. An option on the command line
        changes each default.
        """;

    public bool TryParse(string option, Func<string> next)
    {
        switch (option)
        {
            case "--prevue-ctrl-port": _ctrlPort = int.Parse(next()); return true;
            case "--prevue-ctrl-file": _ctrlFile = next(); return true;
            case "--prevue-feed-trace": _feedTrace = next(); return true;
            default: return false;
        }
    }

    public void Complete(LauncherOptions options)
    {
        if (_feedTrace != null && options.Listing == null)
            throw new ArgumentException("--prevue-feed-trace needs --listing.");

        // ESQ runs from DH1:, and it finds its data files on DF0: and ENV:.
        if (options.Drive is { } drive)
        {
            if (!options.Volumes.Any(volume => volume.Name.Equals("DH1", StringComparison.OrdinalIgnoreCase)))
                options.Volumes.Add(("DH1", drive));
            foreach (var name in new[] { "DF0", "ENV" })
            {
                if (!options.Assigns.Any(assign => assign.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    options.Assigns.Add((name, "DH1:"));
            }

            var esq = Path.Combine(drive, "ESQ");
            if (options.ExecutablePath == null && File.Exists(esq))
                options.ExecutablePath = esq;
        }

        options.CommandName ??= "esq";
        options.Arguments ??= DefaultCode;
        if (!options.FastCpu && options.TurboSeconds == null && options.TurboLabel == null)
        {
            if (options.Listing != null)
                options.TurboLabel = MainLoopLabel;
            else
                options.TurboSeconds = 8;
        }
    }

    public void Start(LauncherContext context)
    {
        var line = context.Own(new PrevueControlLine(context.Core, context.Clock, _ctrlPort, _ctrlFile, context.Log));
        context.StreamRequests.Add(new ControlLineRequests(line.Feed));

        if (_feedTrace != null)
        {
            var writer = context.Own(new StreamWriter(_feedTrace));
            var bases = HunkLayout.Assign(HunkFile.Parse(context.Executable));
            FeedTrace.Create(context.Core, VasmListing.Read(context.Options.Listing!), bases, writer);
        }
    }
}
