using AmigaSharp.Runtime.Exec;

namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// trackdisk.device for a machine with one floppy drive (DF0:). The other units do not exist. Programs read the files
/// of their disks through dos.library and the volumes of the runtime.
/// </summary>
/// <remarks>
/// If DF0: is a volume or an assign of the runtime, the drive has a disk that is not write-protected. Otherwise it is
/// empty. Programs such as ESQ ask for the state of the disk and show a warning if it is missing. The disk has no
/// sectors, so a read or a write of the device fails.
/// </remarks>
public class TrackDiskDevice(Core core) : AbstractDevice(core)
{
    // The commands (devices/trackdisk.h).
    public const ushort CommandRead = 2;
    public const ushort CommandWrite = 3;
    public const ushort CommandUpdate = 4;
    public const ushort CommandClear = 5;
    public const ushort Motor = 9;
    public const ushort Seek = 10;
    public const ushort Format = 11;
    public const ushort Remove = 12;
    public const ushort ChangeNumber = 13;
    public const ushort ChangeState = 14;
    public const ushort ProtectionStatus = 15;
    public const ushort GetDriveType = 18;
    public const ushort GetNumberOfTracks = 19;
    public const ushort AddChangeInterrupt = 20;
    public const ushort RemoveChangeInterrupt = 21;

    /// <summary>TDERR_DiskChanged: no disk is in the drive.</summary>
    public const int ErrorNoDisk = 29;

    /// <summary>TDERR_NotSpecified: a general error.</summary>
    public const int ErrorNotSpecified = 20;

    /// <summary>TDERR_BadUnitNum: the unit does not exist.</summary>
    public const int ErrorBadUnit = 32;

    private const uint DriveType35 = 1;
    private const uint Tracks = 160;

    private readonly List<uint> _changeInterrupts = [];

    public override string Name => "trackdisk.device";
    public override ushort Version => 40;
    public override short LowestOffset => -48;

    public override int OpenUnit(uint unit, uint request, uint flags) => unit == 0 ? 0 : ErrorBadUnit;

    public override void BeginIO(uint request)
    {
        var devices = Core.Devices;
        switch (Command(request))
        {
            // io_Actual is not zero when no disk is in the drive.
            case ChangeState:
                Complete(request, HasDisk ? 0u : 1u);
                break;
            // io_Actual is not zero when the disk is write-protected.
            case ProtectionStatus when HasDisk:
                Complete(request, 0);
                break;
            case ChangeNumber or Motor:
                Complete(request, 0);
                break;
            case GetDriveType:
                Complete(request, DriveType35);
                break;
            case GetNumberOfTracks:
                Complete(request, Tracks);
                break;
            case AddChangeInterrupt:
                // The request stays with the device until the program removes it.
                _changeInterrupts.Add(request);
                break;
            case RemoveChangeInterrupt:
            {
                var interrupt = _changeInterrupts.FirstOrDefault(pending =>
                    Memory.Read32(pending + IoRequestOffsets.Data) == Memory.Read32(request + IoRequestOffsets.Data));
                if (interrupt != 0)
                {
                    _changeInterrupts.Remove(interrupt);
                    devices.Complete(interrupt);
                }

                devices.Complete(request);
                break;
            }
            case CommandUpdate or CommandClear or Seek or Remove:
                devices.Complete(request);
                break;
            case CommandRead or CommandWrite or Format or ProtectionStatus:
                Memory.Write32(request + IoRequestOffsets.Actual, 0);
                devices.Complete(request, HasDisk ? ErrorNotSpecified : ErrorNoDisk);
                break;
            default:
                devices.Complete(request, IoRequestOffsets.ErrorNoCommand);
                break;
        }
    }

    private bool HasDisk => Core.FileSystem.HasName("DF0");

    private void Complete(uint request, uint actual)
    {
        Memory.Write32(request + IoRequestOffsets.Actual, actual);
        Core.Devices.Complete(request);
    }
}
