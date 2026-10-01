using System.Diagnostics;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Host;

/// <summary>
/// Stops a program whose picture does not change for a time, so that a script can start it again. For example, the
/// grid of Prevue always moves, so a picture that does not change means that the program or the emulation stopped
/// working. A stopped emulation makes no new pictures, so the watchdog finds it too.
/// </summary>
/// <remarks>
/// The watchdog runs on its own thread, because the thread of the program can be the thread that stopped working. It
/// compares the picture of the display with the last picture each second.
/// </remarks>
public sealed class Watchdog : IDisposable
{
    private static readonly TimeSpan CheckTime = TimeSpan.FromSeconds(1);

    private readonly Action<uint[]> _copyFrame;
    private readonly TimeSpan _limit;
    private readonly TextWriter _log;
    private readonly Action _stop;
    private uint[] _last;
    private uint[] _current;
    private TimeSpan _lastChange;
    private bool _first = true;
    private bool _fired;
    private readonly Thread? _thread;
    private volatile bool _disposed;

    /// <param name="copyFrame">Copies the picture of the display, for example Display.CopyFrame.</param>
    /// <param name="limit">The time that the picture can stay the same.</param>
    /// <param name="stop">Stops the program. The watchdog calls it one time.</param>
    /// <param name="start">False to not start the thread, for tests that call <see cref="Check"/>.</param>
    /// <param name="height">The height of the picture of the display. The default is the height for NTSC.</param>
    public Watchdog(Action<uint[]> copyFrame, TimeSpan limit, TextWriter log, Action stop, bool start = true,
        int? height = null)
    {
        _last = new uint[Display.Width * (height ?? Display.HeightOf(VideoStandard.Ntsc))];
        _current = new uint[_last.Length];
        _copyFrame = copyFrame;
        _limit = limit;
        _log = log;
        _stop = stop;
        if (!start)
            return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Watchdog" };
        _thread.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _thread?.Join();
    }

    /// <summary>
    /// Compares the picture with the last picture, at a time from the start. Returns true when the watchdog stopped
    /// the program.
    /// </summary>
    public bool Check(TimeSpan now)
    {
        if (_fired)
            return true;
        _copyFrame(_current);
        if (_first || !_current.AsSpan().SequenceEqual(_last))
        {
            _first = false;
            _lastChange = now;
            (_last, _current) = (_current, _last);
            return false;
        }

        if (now - _lastChange < _limit)
            return false;
        _fired = true;
        _log.WriteLine($"The picture did not change for {_limit.TotalSeconds:0} seconds. The watchdog stops the program.");
        _stop();
        return true;
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        while (!_disposed && !Check(clock.Elapsed))
        {
            for (var waited = TimeSpan.Zero; waited < CheckTime && !_disposed; waited += TimeSpan.FromMilliseconds(100))
                Thread.Sleep(100);
        }
    }
}
