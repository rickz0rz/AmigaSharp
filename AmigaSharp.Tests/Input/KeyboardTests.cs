using System.Text;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Input;
using AmigaSharp.Runtime.Libraries.Native;
using AmigaSharp.Tests.Libraries;

namespace AmigaSharp.Tests.Input;

public sealed class KeyboardTests : IDisposable
{
    private const short OpenDevice = -444;
    private const short DoIO = -456;
    private const short RawKeyConvert = -48;

    private readonly LibraryHarness _harness = new();

    [Theory]
    [InlineData(0x20, Qualifier.None, "a")]
    [InlineData(0x20, Qualifier.LeftShift, "A")]
    [InlineData(0x20, Qualifier.CapsLock, "A")]
    [InlineData(0x01, Qualifier.CapsLock, "1")]
    [InlineData(0x01, Qualifier.RightShift, "!")]
    [InlineData(0x33, Qualifier.Control, "\x03")]
    [InlineData(0x44, Qualifier.None, "\r")]
    [InlineData(0x3D, Qualifier.NumericPad, "7")]
    [InlineData(0x4C, Qualifier.None, "\x9B" + "A")]
    [InlineData(0x50, Qualifier.None, "\x9B" + "0~")]
    [InlineData(0x59, Qualifier.LeftShift, "\x9B" + "19~")]
    [InlineData(0x5F, Qualifier.None, "\x9B" + "?~")]
    [InlineData(0xA0, Qualifier.None, "")]
    public void KeyMap_ConvertsRawKeys(int code, Qualifier qualifier, string expected)
    {
        Assert.Equal(expected, Encoding.Latin1.GetString(KeyMap.Convert((ushort)code, qualifier)));
    }

    [Fact]
    public void InputQueue_KeepsTheStateOfTheQualifiers()
    {
        var queue = new InputQueue();

        queue.PostRawKey(RawKey.LeftShift, up: false);
        queue.PostRawKey(0x20, up: false);
        queue.PostRawKey(RawKey.LeftShift, up: true);
        queue.PostRawKey(0x20, up: true);

        var events = new List<InputEventData>();
        while (queue.TryTake(out var value))
            events.Add(value);
        Assert.Equal(Qualifier.LeftShift, events[1].Qualifier);
        Assert.Equal(0x20, events[1].Code);
        Assert.Equal(Qualifier.None, events[3].Qualifier);
        Assert.Equal(0x20 | InputQueue.KeyUp, events[3].Code);
    }

    [Fact]
    public void InputHandler_GetsTheKeyEvents()
    {
        // The handler stores the code of each raw key event at (A1) and returns the chain in D0.
        byte[] code = [0x20, 0x08, 0x22, 0x08, 0x67, 0x10, 0x0C, 0x28, 0x00, 0x01, 0x00, 0x04, 0x66, 0x04, 0x32, 0xA8, 0x00, 0x06, 0x20, 0x50, 0x60, 0xEC, 0x4E, 0x75];
        var memory = _harness.Memory;
        var lastCode = _harness.Core.AllocateSystem(2);
        var handler = _harness.Core.AllocateSystem(InterruptOffsets.Size);
        memory.Write8(handler + NodeOffsets.Priority, 51);
        memory.Write32(handler + InterruptOffsets.Data, lastCode);
        memory.Write32(handler + InterruptOffsets.Code, _harness.Core.AllocateSystem(code));

        var request = OpenDeviceRequest("input.device", 0);
        memory.Write16(request + IoRequestOffsets.Command, InputDevice.CommandAddHandler);
        memory.Write32(request + IoRequestOffsets.Data, handler);
        _harness.Call(_harness.ExecBase, DoIO, ("A1", request));

        _harness.Core.KeyboardInput.PostRawKey(0x45, up: false);
        _harness.Core.PollNow();

        Assert.Equal(0x45, memory.Read16(lastCode));
    }

    [Fact]
    public void ConsoleDevice_OpensAsALibrary_AndConvertsKeys()
    {
        var request = OpenDeviceRequest("console.device", unchecked((uint)-1));
        var console = _harness.Memory.Read32(request + IoRequestOffsets.Device);
        var inputEvent = _harness.Core.AllocateSystem(InputDevice.EventSize);
        _harness.Memory.Write8(inputEvent + InputDevice.EventClass, InputClass.RawKey);
        _harness.Memory.Write16(inputEvent + InputDevice.EventCode, 0x25);
        _harness.Memory.Write16(inputEvent + InputDevice.EventQualifier, (ushort)Qualifier.LeftShift);
        var buffer = _harness.Core.AllocateSystem(8);

        var count = _harness.Call(console, RawKeyConvert, ("A0", inputEvent), ("A1", buffer), ("D1", 8), ("A2", 0));

        Assert.Equal(1u, count);
        Assert.Equal((byte)'H', _harness.Memory.Read8(buffer));
    }

    [Fact]
    public void ConsoleDevice_UnitWithAWindow_DoesNotOpen()
    {
        var request = _harness.Core.AllocateSystem(IoRequestOffsets.StandardSize);

        var error = (int)_harness.Call(_harness.ExecBase, OpenDevice, ("A0", _harness.String("console.device")), ("D0", 0), ("A1", request), ("D1", 0));

        Assert.Equal(IoRequestOffsets.ErrorOpenFail, error);
    }

    [Fact]
    public void TrackDisk_HasDriveZeroWithNoDisk()
    {
        var request = OpenDeviceRequest("trackdisk.device", 0);
        _harness.Memory.Write16(request + IoRequestOffsets.Command, TrackDiskDevice.ChangeState);
        _harness.Call(_harness.ExecBase, DoIO, ("A1", request));
        Assert.Equal(1u, _harness.Memory.Read32(request + IoRequestOffsets.Actual));

        _harness.Memory.Write16(request + IoRequestOffsets.Command, TrackDiskDevice.CommandRead);
        var error = _harness.Call(_harness.ExecBase, DoIO, ("A1", request));
        Assert.Equal((uint)TrackDiskDevice.ErrorNoDisk, error);

        var other = _harness.Core.AllocateSystem(IoRequestOffsets.StandardSize);
        Assert.Equal((uint)TrackDiskDevice.ErrorBadUnit,
            _harness.Call(_harness.ExecBase, OpenDevice, ("A0", _harness.String("trackdisk.device")), ("D0", 1), ("A1", other), ("D1", 0)));
    }

    [Fact]
    public void TrackDisk_HasADiskInDriveZero_WhenDf0IsAVolumeOrAnAssign()
    {
        _harness.Core.FileSystem.AddAssign("DF0", "SYS:");
        var request = OpenDeviceRequest("trackdisk.device", 0);

        _harness.Memory.Write16(request + IoRequestOffsets.Command, TrackDiskDevice.ChangeState);
        _harness.Call(_harness.ExecBase, DoIO, ("A1", request));
        Assert.Equal(0u, _harness.Memory.Read32(request + IoRequestOffsets.Actual));

        _harness.Memory.Write16(request + IoRequestOffsets.Command, TrackDiskDevice.ProtectionStatus);
        Assert.Equal(0u, _harness.Call(_harness.ExecBase, DoIO, ("A1", request)));
        Assert.Equal(0u, _harness.Memory.Read32(request + IoRequestOffsets.Actual));
    }

    private uint OpenDeviceRequest(string name, uint unit)
    {
        var request = _harness.Core.AllocateSystem(IoRequestOffsets.StandardSize);
        var error = _harness.Call(_harness.ExecBase, OpenDevice, ("A0", _harness.String(name)), ("D0", unit), ("A1", request), ("D1", 0));
        Assert.Equal(0u, error);
        return request;
    }

    public void Dispose() => _harness.Dispose();
}
