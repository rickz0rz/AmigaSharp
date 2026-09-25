using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// serial.device on the serial port of Paula. The bytes go through the connection of the serial port, for example
/// <see cref="TcpSerialBridge"/>. A program can also use the hardware registers directly.
/// </summary>
public class SerialDevice(Core core) : AbstractDevice(core)
{
    // struct IOExtSer (devices/serial.h).
    public const uint ControlCharacters = 48;
    public const uint ReadBufferLength = 52;
    public const uint ExtendedFlags = 56;
    public const uint Baud = 60;
    public const uint BreakTime = 64;
    public const uint TerminatorArray = 68;
    public const uint ReadLength = 76;
    public const uint WriteLength = 77;
    public const uint StopBits = 78;
    public const uint SerialFlags = 79;
    public const uint Status = 80;
    public const uint Size = 82;

    // The commands.
    public const ushort CommandReset = 1;
    public const ushort CommandRead = 2;
    public const ushort CommandWrite = 3;
    public const ushort CommandClear = 5;
    public const ushort CommandStop = 6;
    public const ushort CommandStart = 7;
    public const ushort CommandFlush = 8;
    public const ushort CommandQuery = 9;
    public const ushort CommandBreak = 10;
    public const ushort CommandSetParameters = 11;

    /// <summary>SERF_EOFMODE: a read ends at a character of the terminator array.</summary>
    private const byte EndOfFileMode = 0x40;

    private const uint DefaultBaud = 9600;

    private readonly List<uint> _reads = [];

    public override string Name => "serial.device";
    public override ushort Version => 40;
    public override short LowestOffset => -48;

    private SerialPort Port => Core.Chipset.Custom.Serial;

    public override int OpenUnit(uint unit, uint request, uint flags)
    {
        if (unit != 0)
            return IoRequestOffsets.ErrorOpenFail;

        // The default parameters of serial.device.
        Memory.Write32(request + ControlCharacters, 0x1113_0000);
        Memory.Write32(request + ReadBufferLength, 1024);
        Memory.Write32(request + Baud, DefaultBaud);
        Memory.Write32(request + BreakTime, 250_000);
        Memory.Write8(request + ReadLength, 8);
        Memory.Write8(request + WriteLength, 8);
        Memory.Write8(request + StopBits, 1);
        SetBaud(DefaultBaud);

        // serial.device receives with the RBF interrupt, so it enables that interrupt. A program can replace the
        // handler with SetIntVector, and it then depends on this.
        Core.Chipset.Custom.Write(CustomRegister.Intena, 0x8000 | 1 << InterruptBit.Rbf);
        return 0;
    }

    public override void CloseUnit(uint request)
    {
        Core.Chipset.Custom.Write(CustomRegister.Intena, 1 << InterruptBit.Rbf);
    }

    public override void BeginIO(uint request)
    {
        var devices = Core.Devices;
        switch (Command(request))
        {
            case CommandRead:
                Memory.Write32(request + IoRequestOffsets.Actual, 0);
                _reads.Add(request);
                Update();
                break;

            case CommandWrite:
            {
                var data = Memory.Read32(request + IoRequestOffsets.Data);
                var length = (int)Memory.Read32(request + IoRequestOffsets.Length);
                // A length of -1 writes up to the zero byte.
                if (length == -1)
                    length = Memory.ReadCStringBytes(data).Length;
                for (uint i = 0; i < length; i++)
                    Port.WriteData(Memory.Read8(data + i));
                Memory.Write32(request + IoRequestOffsets.Actual, (uint)length);
                devices.Complete(request);
                break;
            }

            case CommandQuery:
                // The low byte has the modem lines of CIA-B: DSR, CTS and CD are 0 when they are active.
                Memory.Write16(request + Status, (ushort)(Core.Chipset.CiaB.Read(0) & 0xF8));
                Memory.Write32(request + IoRequestOffsets.Actual, (uint)(Port.Connection?.Available ?? 0));
                devices.Complete(request);
                break;

            case CommandSetParameters:
                SetBaud(Memory.Read32(request + Baud));
                devices.Complete(request);
                break;

            case CommandReset or CommandClear or CommandStop or CommandStart or CommandFlush or CommandBreak:
                devices.Complete(request);
                break;

            default:
                devices.Complete(request, IoRequestOffsets.ErrorNoCommand);
                break;
        }
    }

    public override int AbortIO(uint request)
    {
        if (!_reads.Remove(request))
            return 0;
        Core.Devices.Complete(request, IoRequestOffsets.ErrorAborted);
        return 0;
    }

    /// <summary>Moves the bytes that arrived to the pending reads, and completes each read that is full.</summary>
    public override void Update()
    {
        foreach (var request in _reads.ToList())
        {
            var data = Memory.Read32(request + IoRequestOffsets.Data);
            var length = Memory.Read32(request + IoRequestOffsets.Length);
            var actual = Memory.Read32(request + IoRequestOffsets.Actual);
            var endOfFile = (Memory.Read8(request + SerialFlags) & EndOfFileMode) != 0;
            var terminators = Memory.ReadBytes(request + TerminatorArray, 8);
            var done = false;
            while (actual < length && Port.Connection?.TryRead(out var value) == true)
            {
                Memory.Write8(data + actual++, value);
                if (endOfFile && terminators.Contains(value))
                {
                    done = true;
                    break;
                }
            }

            Memory.Write32(request + IoRequestOffsets.Actual, actual);
            if (done || actual >= length)
            {
                _reads.Remove(request);
                Core.Devices.Complete(request);
            }
        }
    }

    /// <summary>Sets SERPER for the baud rate, as serial.device does.</summary>
    private void SetBaud(uint baud)
    {
        if (baud == 0)
            return;
        var period = (ushort)Math.Clamp(Beam.ColorClockHz / baud - 1, 1, 0x7FFF);
        Core.Chipset.Custom.Write(CustomRegister.Serper, period);
    }
}
