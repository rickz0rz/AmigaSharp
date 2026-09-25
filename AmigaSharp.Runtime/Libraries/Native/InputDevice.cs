using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Input;

namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// input.device: sends the input events to the handlers that programs add with IND_ADDHANDLER. The keyboard events
/// come from <see cref="Core.KeyboardInput"/>, and a timer event comes each tenth of a second, as on the Amiga.
/// </summary>
/// <remarks>
/// The handlers run at safe points in the task that runs at the time, not in a task of the device.
/// </remarks>
public class InputDevice : AbstractDevice
{
    public const ushort CommandAddHandler = 9;
    public const ushort CommandRemoveHandler = 10;
    public const ushort CommandWriteEvent = 11;

    // struct InputEvent (devices/inputevent.h).
    public const uint EventNext = 0;
    public const uint EventClass = 4;
    public const uint EventSubClass = 5;
    public const uint EventCode = 6;
    public const uint EventQualifier = 8;
    public const uint EventX = 10;
    public const uint EventY = 12;
    public const uint EventTimeStamp = 14;
    public const uint EventSize = 22;

    private const int MaximumEvents = 16;
    private static readonly TimeSpan TimerInterval = TimeSpan.FromSeconds(0.1);

    private readonly List<uint> _handlers = [];
    private uint _events;
    private TimeSpan _nextTimer;
    private bool _delivering;

    public InputDevice(Core core) : base(core)
    {
        core.AddPollHandler(Poll);
    }

    public override string Name => "input.device";
    public override ushort Version => 40;
    public override short LowestOffset => -54;

    public override void BeginIO(uint request)
    {
        switch (Command(request))
        {
            case CommandAddHandler:
                _handlers.Add(Memory.Read32(request + IoRequestOffsets.Data));
                // The handlers are in the order of their priorities, the highest first.
                _handlers.Sort((a, b) => ((sbyte)Memory.Read8(b + NodeOffsets.Priority)).CompareTo((sbyte)Memory.Read8(a + NodeOffsets.Priority)));
                break;
            case CommandRemoveHandler:
                _handlers.Remove(Memory.Read32(request + IoRequestOffsets.Data));
                break;
            case CommandWriteEvent:
            {
                var input = Memory.Read32(request + IoRequestOffsets.Data);
                Deliver([new InputEventData(Memory.Read8(input + EventClass), Memory.Read16(input + EventCode),
                    (Qualifier)Memory.Read16(input + EventQualifier))]);
                break;
            }
        }

        Core.Devices.Complete(request);
    }

    /// <summary>Sends the waiting events and the timer events to the handlers.</summary>
    private void Poll()
    {
        if (_handlers.Count == 0 || _delivering || Core.InNativeCall || Core.Cpu.S)
            return;

        var events = new List<InputEventData>();
        var now = Core.Chipset.Beam.Clock.Elapsed;
        if (now >= _nextTimer)
        {
            _nextTimer = now + TimerInterval;
            events.Add(new InputEventData(InputClass.Timer, 0, Qualifier.None));
        }

        while (events.Count < MaximumEvents && Core.KeyboardInput.TryTake(out var value))
            events.Add(value);
        if (events.Count > 0)
            Deliver(events);
    }

    /// <summary>
    /// Makes a chain of InputEvent structures and calls each handler with it: A0 is the chain and A1 is is_Data. A
    /// handler returns the chain in D0, and it can remove events from the chain.
    /// </summary>
    private void Deliver(IReadOnlyList<InputEventData> events)
    {
        if (_handlers.Count == 0)
            return;
        if (_events == 0)
            _events = Core.AllocateSystem(EventSize * MaximumEvents);

        var time = Core.Chipset.Beam.Clock.Elapsed;
        for (var i = 0; i < events.Count; i++)
        {
            var address = _events + (uint)i * EventSize;
            Memory.Write32(address + EventNext, i + 1 < events.Count ? address + EventSize : 0);
            Memory.Write8(address + EventClass, events[i].Class);
            Memory.Write8(address + EventSubClass, 0);
            Memory.Write16(address + EventCode, events[i].Code);
            Memory.Write16(address + EventQualifier, (ushort)events[i].Qualifier);
            Memory.Write32(address + EventX, 0);
            Memory.Write32(address + EventTimeStamp, (uint)time.TotalSeconds);
            Memory.Write32(address + EventTimeStamp + 4, (uint)(time.Ticks / 10 % 1_000_000));
        }

        var cpu = Core.Cpu;
        var saved = CpuSnapshot.Take(cpu);
        _delivering = true;
        try
        {
            var chain = _events;
            foreach (var handler in _handlers.ToList())
            {
                if (chain == 0)
                    break;
                cpu.A[0] = chain;
                cpu.A[1] = Memory.Read32(handler + InterruptOffsets.Data);
                cpu.A[6] = Core.ExecBase;
                Core.CallFromNative(Memory.Read32(handler + InterruptOffsets.Code));
                chain = cpu.D[0];
            }
        }
        finally
        {
            _delivering = false;
            saved.Restore(cpu);
        }
    }
}
