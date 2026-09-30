using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Host;

/// <summary>
/// Adds the parts for one program to a launcher. For example, AmigaSharp.PrevueLauncher adds the control line of
/// Prevue, its requests on the port of the stream, and its defaults.
/// </summary>
public interface ILauncherExtension
{
    /// <summary>The usage of the options of the extension, with a heading. The launcher adds it to its usage.</summary>
    string Usage { get; }

    /// <summary>Reads an option that the launcher does not know. Returns false if the option is not of the extension.</summary>
    /// <param name="next">Gives the value after the option.</param>
    bool TryParse(string option, Func<string> next);

    /// <summary>Sets defaults and checks the options of the extension, after the launcher read all the options.</summary>
    /// <exception cref="ArgumentException">The options are not correct.</exception>
    void Complete(LauncherOptions options);

    /// <summary>
    /// Sets up the parts of the extension. The launcher calls it after it made the core and connected the serial
    /// port, and before it starts the stream and the program.
    /// </summary>
    void Start(LauncherContext context);
}

/// <summary>What an extension can use and change when it starts.</summary>
public sealed class LauncherContext(Core core, IClock clock, TextWriter log, LauncherOptions options, byte[] executable)
    : IDisposable
{
    private readonly List<IDisposable> _parts = [];

    public Core Core => core;
    public IClock Clock => clock;
    public TextWriter Log => log;
    public LauncherOptions Options => options;

    /// <summary>The bytes of the executable of the program.</summary>
    public byte[] Executable => executable;

    /// <summary>More requests for the port of the stream (see <see cref="IStreamRequests"/>).</summary>
    public List<IStreamRequests> StreamRequests { get; } = [];

    /// <summary>More keys for the segments of a schedule (see <see cref="Schedule"/>).</summary>
    public List<IScheduleExtension> ScheduleExtensions { get; } = [];

    /// <summary>Adds a part that the launcher disposes when the program ends.</summary>
    public T Own<T>(T part) where T : IDisposable
    {
        _parts.Add(part);
        return part;
    }

    public void Dispose()
    {
        for (var i = _parts.Count - 1; i >= 0; i--)
            _parts[i].Dispose();
    }
}
