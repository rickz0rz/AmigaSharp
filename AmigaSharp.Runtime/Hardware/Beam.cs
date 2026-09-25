using System.Diagnostics;

namespace AmigaSharp.Runtime.Hardware;

/// <summary>A source of time for the emulated hardware.</summary>
public interface IClock
{
    TimeSpan Elapsed { get; }

    /// <summary>Returns when the time is at least <paramref name="time"/>.</summary>
    void WaitUntil(TimeSpan time);
}

/// <summary>Real time from the start of the runtime.</summary>
public sealed class RealTimeClock : IClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void WaitUntil(TimeSpan time)
    {
        var remaining = time - Elapsed;
        if (remaining > TimeSpan.Zero)
            Thread.Sleep(remaining);
    }
}

/// <summary>A clock that moves only when a test moves it.</summary>
public sealed class ManualClock : IClock
{
    public TimeSpan Elapsed { get; set; }

    public void Advance(TimeSpan time) => Elapsed += time;

    /// <summary>Moves the clock to the time at once.</summary>
    public void WaitUntil(TimeSpan time)
    {
        if (time > Elapsed)
            Elapsed = time;
    }
}

/// <summary>
/// The position of the video beam of an NTSC Amiga: 262 lines of 227 color clocks, about 60 frames each second.
/// </summary>
public sealed class Beam(IClock clock)
{
    /// <summary>The NTSC color clock: 3.579545 MHz.</summary>
    public const double ColorClockHz = 3_579_545.0;

    public const int ColorClocksPerLine = 227;
    public const int LinesPerFrame = 262;

    public IClock Clock { get; } = clock;

    private long ColorClocks => (long)(Clock.Elapsed.TotalSeconds * ColorClockHz);

    /// <summary>The number of lines since the start.</summary>
    public long TotalLines => ColorClocks / ColorClocksPerLine;

    /// <summary>The number of frames since the start.</summary>
    public long Frame => TotalLines / LinesPerFrame;

    /// <summary>The time at which a frame starts. Half a color clock more makes sure that the frame has started.</summary>
    public TimeSpan StartOfFrame(long frame) =>
        TimeSpan.FromSeconds((frame * LinesPerFrame * ColorClocksPerLine + 0.5) / ColorClockHz);

    /// <summary>The line in the frame, 0 to 261.</summary>
    public int Line => (int)(TotalLines % LinesPerFrame);

    /// <summary>The horizontal position in color clocks, 0 to 226.</summary>
    public int Horizontal => (int)(ColorClocks % ColorClocksPerLine);
}
