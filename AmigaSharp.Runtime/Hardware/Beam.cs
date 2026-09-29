using System.Diagnostics;

namespace AmigaSharp.Runtime.Hardware;

/// <summary>A source of time for the emulated hardware.</summary>
public interface IClock
{
    TimeSpan Elapsed { get; }

    /// <summary>Returns when the time is at least <paramref name="time"/>.</summary>
    void WaitUntil(TimeSpan time);

    /// <summary>The runtime calls this at each full safe point. A virtual clock moves forward here.</summary>
    void Tick()
    {
    }

    /// <summary>True if the time is the time of the host. The runtime then runs the CPU at the speed of a 68000.</summary>
    bool IsRealTime => false;
}

/// <summary>Real time from the start of the clock.</summary>
public sealed class RealTimeClock : IClock
{
    private readonly Stopwatch _stopwatch = new();

    /// <param name="start">
    /// False to keep the time at 0 until <see cref="Start"/>. A host starts the clock when the program starts, so that
    /// the time to load and compile the program does not count as Amiga time.
    /// </param>
    public RealTimeClock(bool start = true)
    {
        if (start)
            _stopwatch.Start();
    }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public bool IsRealTime => true;

    /// <summary>Starts the clock if it is stopped.</summary>
    public void Start() => _stopwatch.Start();

    /// <remarks>
    /// Thread.Sleep has a resolution of 1 ms, but the audio interrupts of a program can come each 0.9 ms. So on macOS
    /// and Linux, the clock uses usleep, which can sleep for less than 1 ms. On Windows, Thread.Sleep waits for the
    /// next tick of the system timer, which is 15.6 ms by default. So the clock uses a high-resolution waitable timer
    /// (Windows 10 1803 and later), which can also sleep for less than 1 ms.
    /// </remarks>
    public void WaitUntil(TimeSpan time)
    {
        var remaining = time - Elapsed;
        if (remaining <= TimeSpan.Zero)
            return;
        if (OperatingSystem.IsWindows())
            WindowsTimer.Sleep(remaining);
        else
            usleep((uint)Math.Max(1, remaining.TotalMicroseconds));
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int usleep(uint microseconds);

    /// <summary>A high-resolution waitable timer for each thread that waits.</summary>
    private static class WindowsTimer
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;
        private const uint Infinite = 0xFFFFFFFF;

        [ThreadStatic] private static IntPtr _timer;

        public static void Sleep(TimeSpan time)
        {
            if (_timer == IntPtr.Zero)
                _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (_timer == IntPtr.Zero)
            {
                // Older versions of Windows do not have high-resolution timers.
                Thread.Sleep(time);
                return;
            }

            // A negative due time is relative, in units of 100 ns.
            var dueTime = -Math.Max(1, time.Ticks);
            if (SetWaitableTimerEx(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                WaitForSingleObject(_timer, Infinite);
            else
                Thread.Sleep(time);
        }

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion,
            IntPtr argument, IntPtr wakeContext, uint tolerableDelay);

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    }
}

/// <summary>A clock that also measures the time that the program waits in <see cref="WaitUntil"/>.</summary>
public sealed class MeasuringClock(IClock clock) : IClock
{
    private long _waitTicks;

    public TimeSpan Elapsed => clock.Elapsed;

    public void Tick() => clock.Tick();

    public bool IsRealTime => clock.IsRealTime;

    /// <summary>The time in <see cref="WaitUntil"/>, from all threads.</summary>
    public TimeSpan WaitTime => TimeSpan.FromTicks(Interlocked.Read(ref _waitTicks));

    public void WaitUntil(TimeSpan time)
    {
        var start = Stopwatch.GetTimestamp();
        clock.WaitUntil(time);
        Interlocked.Add(ref _waitTicks, Stopwatch.GetElapsedTime(start).Ticks);
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
/// A clock that is not real time: it moves forward a fixed step at each safe point, and a wait moves it to the end of
/// the wait at once. So a run is the same each time, and it runs as fast as the host can.
/// </summary>
public sealed class VirtualClock(TimeSpan step) : IClock
{
    private long _ticks;

    /// <summary>A step of 100 microseconds, about the time of a library call or a short loop on a 68000.</summary>
    public VirtualClock() : this(TimeSpan.FromMicroseconds(100))
    {
    }

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public void WaitUntil(TimeSpan time)
    {
        if (time > Elapsed)
            Interlocked.Exchange(ref _ticks, time.Ticks);
    }

    public void Tick() => Interlocked.Add(ref _ticks, step.Ticks);
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

    /// <summary>The number of color clocks since the start.</summary>
    public long ColorClocks => (long)(Clock.Elapsed.TotalSeconds * ColorClockHz);

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
