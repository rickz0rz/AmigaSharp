using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Host;

/// <summary>
/// Actions at times of the Amiga clock, for example a screenshot or a key press. The 68000 thread runs each action at
/// its first safe point at or after the time. So with a virtual clock, an action happens at the same point of the
/// program in each run, and a screenshot shows the same frame.
/// </summary>
public sealed class TimedActions
{
    private readonly IClock _clock;
    private readonly PriorityQueue<Action, (double Seconds, int Order)> _queue = new();
    private readonly object _lock = new();
    private int _order;

    public TimedActions(Core core)
    {
        _clock = core.Chipset.Beam.Clock;
        core.AddPollHandler(Run);
    }

    /// <summary>Adds an action at the time in seconds. Actions at the same time run in the order of their adds.</summary>
    public void Add(double seconds, Action action)
    {
        lock (_lock)
            _queue.Enqueue(action, (seconds, _order++));
    }

    private void Run()
    {
        while (true)
        {
            Action action;
            lock (_lock)
            {
                if (!_queue.TryPeek(out action!, out var when) || _clock.Elapsed.TotalSeconds < when.Seconds)
                    return;
                _queue.Dequeue();
            }

            action();
        }
    }
}
