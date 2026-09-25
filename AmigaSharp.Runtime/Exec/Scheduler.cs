namespace AmigaSharp.Runtime.Exec;

/// <summary>
/// The task scheduler of exec. Each Amiga task runs on its own host thread, because the translated code of a task
/// keeps its calls on the C# stack. Only one task runs at a time. The others wait for their turn.
/// </summary>
/// <remarks>
/// A task gives the CPU to another task when it waits (exec Wait or dos Delay), and at a safe point when its time
/// slice ends. The time slice is 4 frames, as in exec. A ready task with a higher priority runs first. Forbid stops
/// the switches at safe points, and the scheduler does not switch in an interrupt or in a call from native code.
/// Each task has its own registers and its own nest counts of Forbid and Disable.
/// </remarks>
public sealed class Scheduler
{
    /// <summary>The return address of the first function of a task. When the function returns, the task ends.</summary>
    public const uint TaskExitAddress = 0x00FF_FFE8;

    private static readonly TimeSpan TimeSlice = TimeSpan.FromSeconds(4 / 60.0);

    private readonly Core _core;
    private readonly List<TaskState> _tasks = [];
    private TaskState _current;
    private TimeSpan _sliceStart;

    private sealed class TaskState
    {
        public required uint Address { get; init; }
        public SemaphoreSlim Resume { get; } = new(0);
        public CpuSnapshot Registers { get; set; }
        public Core.CallState Calls { get; set; }
        public byte InterruptDisableCount { get; set; } = 0xFF;
        public byte TaskDisableCount { get; set; } = 0xFF;

        /// <summary>True while the task waits for signals or for a time.</summary>
        public bool Waiting { get; set; }

        public uint WaitMask { get; set; }

        /// <summary>The time at which a Delay ends.</summary>
        public TimeSpan? WakeTime { get; set; }

        public bool Finished { get; set; }
    }

    public Scheduler(Core core, uint mainTask)
    {
        _core = core;
        _current = new TaskState { Address = mainTask };
        _tasks.Add(_current);
    }

    /// <summary>The task that runs now.</summary>
    public uint Current => _current.Address;

    /// <summary>All tasks that have not ended.</summary>
    public IEnumerable<uint> Tasks => _tasks.Select(task => task.Address);

    /// <summary>
    /// Starts a task. It runs the code at <paramref name="entry"/> in user mode with the stack at
    /// <paramref name="stackTop"/>. It becomes ready now, and it runs when the scheduler gives it the CPU.
    /// </summary>
    public void Start(uint task, uint entry, uint stackTop)
    {
        var registers = CpuSnapshot.Take(_core.Cpu);
        Array.Clear(registers.D);
        Array.Clear(registers.A);
        var state = new TaskState
        {
            Address = task,
            Registers = registers with { Sr = 0, Usp = stackTop, Pc = entry },
        };
        _tasks.Add(state);

        var thread = new Thread(() => RunTask(state, entry), 64 * 1024 * 1024)
        {
            IsBackground = true,
            Name = $"Task ${task:X6}",
        };
        thread.Start();
    }

    /// <summary>Makes a waiting task ready if the signals are the signals that it waits for.</summary>
    public void Signalled(uint task, uint signals)
    {
        var state = _tasks.FirstOrDefault(candidate => candidate.Address == task);
        if (state is { Waiting: true } && (state.WaitMask & signals) != 0)
            state.Waiting = false;
    }

    /// <summary>
    /// The current task waits for signals. Gives the CPU to another ready task, if one is ready. Returns false if no
    /// other task is ready, and the caller must wait for an interrupt.
    /// </summary>
    public bool WaitForOtherTask(uint mask)
    {
        _current.Waiting = true;
        _current.WaitMask = mask;
        return SwitchToReadyTask(includeCurrent: false);
    }

    /// <summary>The current task no longer waits.</summary>
    public void StopWaiting()
    {
        _current.Waiting = false;
        _current.WaitMask = 0;
        _current.WakeTime = null;
    }

    /// <summary>True if another task can run now or later: it is ready, or its Delay ends.</summary>
    public bool OtherTaskCanRun => _tasks.Any(task => task != _current && !task.Finished && (!task.Waiting || task.WakeTime != null));

    /// <summary>dos Delay: the current task waits for the time. Other tasks run in the meantime.</summary>
    public void Delay(TimeSpan time)
    {
        var clock = _core.Chipset.Beam.Clock;
        var wake = clock.Elapsed + time;
        _current.Waiting = true;
        _current.WaitMask = 0;
        _current.WakeTime = wake;
        try
        {
            while (clock.Elapsed < wake)
            {
                _core.PollNow();
                if (SwitchToReadyTask(includeCurrent: false))
                    continue;
                clock.WaitUntil(Min(wake, clock.Elapsed + TimeSpan.FromMilliseconds(1)));
            }
        }
        finally
        {
            StopWaiting();
        }
    }

    /// <summary>
    /// A safe point: gives the CPU to another ready task if the time slice of the current task ended, or if a task with
    /// a higher priority is ready.
    /// </summary>
    public void Preempt()
    {
        if (_tasks.Count <= 1 || _core.Cpu.S || _core.InNativeCall)
            return;
        if ((sbyte)_core.Memory.Read8(_core.ExecBase + ExecBaseOffsets.TaskDisableCount) >= 0)
            return;

        var now = _core.Chipset.Beam.Clock.Elapsed;
        var next = NextReadyTask(includeCurrent: false);
        if (next == null)
            return;
        if (Priority(next) > Priority(_current) || (Priority(next) == Priority(_current) && now - _sliceStart >= TimeSlice))
            SwitchTo(next);
    }

    private bool SwitchToReadyTask(bool includeCurrent)
    {
        var next = NextReadyTask(includeCurrent);
        if (next == null || next == _current)
            return false;
        SwitchTo(next);
        return true;
    }

    /// <summary>The ready task with the highest priority. Tasks with equal priority take turns.</summary>
    private TaskState? NextReadyTask(bool includeCurrent)
    {
        var now = _core.Chipset.Beam.Clock.Elapsed;
        var start = _tasks.IndexOf(_current);
        TaskState? best = null;
        for (var i = 1; i <= _tasks.Count; i++)
        {
            var task = _tasks[(start + i) % _tasks.Count];
            if (task == _current && !includeCurrent)
                continue;
            var ready = !task.Finished && (!task.Waiting || (task.WakeTime is { } wake && now >= wake));
            if (ready && (best == null || Priority(task) > Priority(best)))
                best = task;
        }

        return best;
    }

    /// <summary>Saves the state of the current task, lets the next task run, and waits until this task runs again.</summary>
    private void SwitchTo(TaskState next)
    {
        var me = _current;
        Save(me);
        Activate(next);
        next.Resume.Release();
        me.Resume.Wait();
        Restore(me);
    }

    private void Save(TaskState task)
    {
        var memory = _core.Memory;
        task.Registers = CpuSnapshot.Take(_core.Cpu);
        task.Calls = _core.SaveCallState();
        task.InterruptDisableCount = memory.Read8(_core.ExecBase + ExecBaseOffsets.InterruptDisableCount);
        task.TaskDisableCount = memory.Read8(_core.ExecBase + ExecBaseOffsets.TaskDisableCount);
    }

    /// <summary>Makes the task the current task. Its thread restores its registers when it runs.</summary>
    private void Activate(TaskState task)
    {
        var memory = _core.Memory;
        _current = task;
        _sliceStart = _core.Chipset.Beam.Clock.Elapsed;
        memory.Write32(_core.ExecBase + ExecBaseOffsets.ThisTask, task.Address);
        memory.Write8(_core.ExecBase + ExecBaseOffsets.InterruptDisableCount, task.InterruptDisableCount);
        memory.Write8(_core.ExecBase + ExecBaseOffsets.TaskDisableCount, task.TaskDisableCount);
    }

    private void Restore(TaskState task)
    {
        task.Registers.Restore(_core.Cpu);
        _core.RestoreCallState(task.Calls);
    }

    private void RunTask(TaskState state, uint entry)
    {
        state.Resume.Wait();
        Restore(state);
        try
        {
            _core.CallAddress(TaskExitAddress, entry);
        }
        catch (Exception e) when (e is not StackUnwindException)
        {
            _core.Log.WriteLine($"Task ${state.Address:X6} stopped: {e.GetType().Name}: {e.Message}");
        }
        catch (StackUnwindException e)
        {
            _core.Log.WriteLine($"Task ${state.Address:X6} returned to ${e.ReturnAddress:X6}.");
        }

        Exit(state);
    }

    /// <summary>Ends a task and gives the CPU to the next ready task. If no task is ready, waits for one here.</summary>
    private void Exit(TaskState state)
    {
        state.Finished = true;
        _tasks.Remove(state);
        var clock = _core.Chipset.Beam.Clock;
        while (true)
        {
            var next = NextReadyTask(includeCurrent: true);
            if (next != null)
            {
                Activate(next);
                next.Resume.Release();
                return;
            }

            // No task can run: let the interrupts run until one of them makes a task ready.
            _core.PollNow();
            clock.WaitUntil(clock.Elapsed + TimeSpan.FromMilliseconds(1));
        }
    }

    private sbyte Priority(TaskState task) => (sbyte)_core.Memory.Read8(task.Address + NodeOffsets.Priority);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
