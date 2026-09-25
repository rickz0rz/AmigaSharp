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
        if (core.Libraries.Open(name, 0) is not AbstractDevice device)
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
        var device = DeviceOf(request);
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
        DeviceOf(request).BeginIO(request);
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

    public int Abort(uint request) => IsComplete(request) ? 0 : DeviceOf(request).AbortIO(request);

    /// <summary>The signal of the reply port of the request.</summary>
    public uint ReplySignal(uint request)
    {
        var port = core.Memory.Read32(request + MessageOffsets.ReplyPort);
        return port == 0 ? 0 : 1u << core.Memory.Read8(port + MsgPortOffsets.SignalBit);
    }

    private AbstractDevice DeviceOf(uint request)
    {
        var device = core.Memory.Read32(request + IoRequestOffsets.Device);
        return core.Libraries.FindByBase(device) as AbstractDevice
               ?? throw new InvalidOperationException($"The I/O request at ${request:X6} has no open device.");
    }
}
