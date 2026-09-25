using AmigaSharp.Runtime.Exec;

namespace AmigaSharp.Runtime.Libraries;

/// <summary>
/// An HLE device. A device is a library with a base and a jump table. The runtime calls the methods below for
/// OpenDevice, CloseDevice, BeginIO and AbortIO.
/// </summary>
public abstract class AbstractDevice(Core core) : AbstractLibrary
{
    protected Core Core { get; } = core;
    protected Memory Memory => Core.Memory;

    /// <summary>Opens a unit. Returns 0, or an error code such as <see cref="IoRequestOffsets.ErrorOpenFail"/>.</summary>
    public virtual int OpenUnit(uint unit, uint request, uint flags) => 0;

    public virtual void CloseUnit(uint request)
    {
    }

    /// <summary>
    /// Starts a request. Call <see cref="DeviceManager.Complete"/> when the request is done, now or later.
    /// </summary>
    public abstract void BeginIO(uint request);

    /// <summary>Stops a request that is not done. Returns 0, or an error code.</summary>
    public virtual int AbortIO(uint request) => 0;

    protected ushort Command(uint request) => Memory.Read16(request + IoRequestOffsets.Command);
}
