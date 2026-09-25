using AmigaSharp.Runtime.Exec;

namespace AmigaSharp.Runtime.Libraries;

/// <summary>
/// Opens the HLE devices and routes the I/O requests to them. A request is pending from BeginIO until the device
/// calls <see cref="Complete"/>.
/// </summary>
public sealed class DeviceManager(Core core)
{
    private readonly HashSet<uint> _pending = [];

    /// <summary>OpenDevice: returns 0, or an error code.</summary>
    public int Open(string name, uint unit, uint request, uint flags)
    {
        var memory = core.Memory;
        if (core.Libraries.Open(name, 0, logMissing: false) is not AbstractDevice device)
        {
            core.Log.WriteLine($"OpenDevice: the runtime has no {name}.");
            return IoRequestOffsets.ErrorOpenFail;
        }

        memory.Write32(request + IoRequestOffsets.Device, device.Base);
        memory.Write8(request + IoRequestOffsets.Error, 0);
        var error = device.OpenUnit(unit, request, flags);
        if (error != 0)
        {
            core.Libraries.Close(device.Base);
            memory.Write32(request + IoRequestOffsets.Device, 0);
        }

        return error;
    }

    public void Close(uint request)
    {
        if (DeviceOf(request) is not { } device)
            return;
        device.CloseUnit(request);
        core.Libraries.Close(device.Base);
        core.Memory.Write32(request + IoRequestOffsets.Device, 0xFFFF_FFFF);
    }

    public void BeginIO(uint request)
    {
        var memory = core.Memory;
        // The request is a message that the device replies to when it is done.
        memory.Write8(request + NodeOffsets.Type, NodeType.Message);
        memory.Write8(request + IoRequestOffsets.Error, 0);
        _pending.Add(request);
        if (DeviceOf(request) is { } device)
            device.BeginIO(request);
        else
            Complete(request, IoRequestOffsets.ErrorOpenFail);
    }

    /// <summary>
    /// The device calls this method when a request is done. A quick request that completes in BeginIO is not replied.
    /// The others go to the reply port.
    /// </summary>
    public void Complete(uint request, int error = 0)
    {
        var memory = core.Memory;
        memory.Write8(request + IoRequestOffsets.Error, (byte)error);
        _pending.Remove(request);
        if ((memory.Read8(request + IoRequestOffsets.Flags) & IoRequestOffsets.FlagQuick) != 0)
        {
            memory.Write8(request + NodeOffsets.Type, NodeType.ReplyMessage);
            return;
        }

        memory.Write8(request + NodeOffsets.Type, NodeType.ReplyMessage);
        core.DeliverMessage(memory.Read32(request + MessageOffsets.ReplyPort), request);
    }

    public bool IsComplete(uint request) => !_pending.Contains(request);

    /// <summary>True if a request is not done yet. A device can then still send a signal.</summary>
    public bool HasPendingRequests => _pending.Count > 0;

    /// <summary>Lets each device with a pending request do its work, for example read the bytes that arrived.</summary>
    public void Update()
    {
        if (_pending.Count == 0)
            return;
        foreach (var device in core.Libraries.Loaded.OfType<AbstractDevice>().ToList())
            device.Update();
    }

    public int Abort(uint request) => IsComplete(request) || DeviceOf(request) is not { } device ? 0 : device.AbortIO(request);

    /// <summary>The signal of the reply port of the request.</summary>
    public uint ReplySignal(uint request)
    {
        var port = core.Memory.Read32(request + MessageOffsets.ReplyPort);
        return port == 0 ? 0 : 1u << core.Memory.Read8(port + MsgPortOffsets.SignalBit);
    }

    /// <summary>
    /// The device of a request, or null if the request has no open device. A program can use a request after
    /// OpenDevice failed, for example in its cleanup code. The runtime then ignores the request and logs it.
    /// </summary>
    private AbstractDevice? DeviceOf(uint request)
    {
        if (request == 0)
            return null;
        var address = core.Memory.Read32(request + IoRequestOffsets.Device);
        if (core.Libraries.FindByBase(address) is AbstractDevice device)
            return device;
        core.Log.WriteLine($"The I/O request at ${request:X6} has no open device.");
        return null;
    }
}
