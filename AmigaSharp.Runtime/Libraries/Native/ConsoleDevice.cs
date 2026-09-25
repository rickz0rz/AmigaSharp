using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Input;

namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// console.device. The runtime has no console windows, so only unit -1 (CONU_LIBRARY) opens. A program opens it to
/// call RawKeyConvert, which converts keyboard events to characters.
/// </summary>
public class ConsoleDevice(Core core) : AbstractDevice(core)
{
    private const uint LibraryUnit = 0xFFFF_FFFF;

    public override string Name => "console.device";
    public override ushort Version => 40;
    public override short LowestOffset => -48;

    public override int OpenUnit(uint unit, uint request, uint flags)
    {
        if (unit == LibraryUnit)
            return 0;
        Core.Log.WriteLine($"console.device: unit {unit} needs a window, and the runtime has no windows.");
        return IoRequestOffsets.ErrorOpenFail;
    }

    public override void BeginIO(uint request) => Core.Devices.Complete(request, IoRequestOffsets.ErrorNoCommand);

    // events = CDInputHandler(events, consoleDevice)
    // D0                      A0      A1
    [LibraryFunctionOffset(-42)]
    public uint CDInputHandler([A0] uint events, [A1] uint device) => events;

    // actual = RawKeyConvert(event, buffer, length, keyMap)
    // D0                     A0     A1      D1      A2
    // Returns the number of characters, or -1 if the buffer is too small.
    [LibraryFunctionOffset(-48)]
    public int RawKeyConvert([A0] uint inputEvent, [A1] uint buffer, [D1] int length, [A2] uint keyMap)
    {
        if (Memory.Read8(inputEvent + InputDevice.EventClass) != InputClass.RawKey)
            return 0;
        var characters = KeyMap.Convert(Memory.Read16(inputEvent + InputDevice.EventCode),
            (Qualifier)Memory.Read16(inputEvent + InputDevice.EventQualifier));
        if (characters.Length > length)
            return -1;
        Memory.WriteBytes(buffer, characters);
        return characters.Length;
    }
}
