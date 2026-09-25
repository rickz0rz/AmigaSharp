namespace AmigaSharp.Runtime.Exec;

/// <summary>The program called exec ColdReboot.</summary>
public sealed class ColdRebootException() : Exception("The program called ColdReboot.");

/// <summary>The program called exec Alert. A real Amiga shows a "Guru Meditation" for a dead-end alert.</summary>
public sealed class AlertException(uint number) : Exception($"The program called Alert ${number:X8}.")
{
    public uint Number { get; } = number;
}

/// <summary>
/// The program waits for signals that nothing can send. The runtime has one task, so the wait can never end.
/// </summary>
public sealed class WaitDeadlockException(uint signals)
    : Exception($"The program waits for signals ${signals:X8}, but no device, interrupt or task can send them.")
{
    public uint Signals { get; } = signals;
}
