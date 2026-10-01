namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// Encodes the tracks of an ADF as the MFM data of an AmigaDOS disk, as trackdisk.device writes them.
/// </summary>
/// <remarks>
/// A track has 11 sectors of 544 words, then a gap. A sector has two MFM zero words ($AAAA), two sync words ($4489), the
/// info long (format $FF, track, sector, sectors to the gap), 16 bytes of label, the header checksum, the data
/// checksum and the 512 bytes of data. Each block of longs is split: first the odd bits of all longs, then the even
/// bits. A checksum is the XOR of the data bits of its block. Then each clock bit is 1 if the data bits on both sides
/// of it are 0.
/// </remarks>
public static class AmigaDosMfm
{
    public const int SectorsPerTrack = 11;
    public const int SectorBytes = 512;
    public const int TrackBytes = SectorsPerTrack * SectorBytes;

    /// <summary>The words of a track at 300 RPM, with the gap.</summary>
    public const int TrackWords = 6334;

    public const ushort Sync = 0x4489;
    private const uint DataBits = 0x5555_5555;
    private const int SectorLongs = 272;

    /// <summary>Encodes track <paramref name="track"/> (cylinder times 2 plus the head) of the disk image.</summary>
    public static ushort[] EncodeTrack(ReadOnlySpan<byte> image, int track)
    {
        var words = new ushort[TrackWords];
        Array.Fill(words, (ushort)0xAAAA);
        for (var sector = 0; sector < SectorsPerTrack; sector++)
        {
            var data = image.Slice((track * SectorsPerTrack + sector) * SectorBytes, SectorBytes);
            var longs = EncodeSector(track, sector, data);
            for (var i = 0; i < longs.Length; i++)
            {
                words[sector * SectorLongs * 2 + i * 2] = (ushort)(longs[i] >> 16);
                words[sector * SectorLongs * 2 + i * 2 + 1] = (ushort)longs[i];
            }
        }

        // An MFM zero word after a data bit of 1 has no clock bit in front: $2AAA, not $AAAA.
        for (var i = 1; i < words.Length; i++)
        {
            if (words[i] == 0xAAAA && (words[i - 1] & 1) != 0)
                words[i] = 0x2AAA;
        }

        return words;
    }

    private static uint[] EncodeSector(int track, int sector, ReadOnlySpan<byte> data)
    {
        var info = 0xFF00_0000u | (uint)track << 16 | (uint)sector << 8 | (uint)(SectorsPerTrack - sector);
        var header = new List<uint>();
        Split([info], header);
        Split([0, 0, 0, 0], header);
        var headerChecksum = Checksum(header);

        var dataLongs = new uint[SectorBytes / 4];
        for (var i = 0; i < dataLongs.Length; i++)
            dataLongs[i] = (uint)(data[i * 4] << 24 | data[i * 4 + 1] << 16 | data[i * 4 + 2] << 8 | data[i * 4 + 3]);
        var dataBlock = new List<uint>();
        Split(dataLongs, dataBlock);
        var dataChecksum = Checksum(dataBlock);

        var longs = new List<uint> { 0xAAAA_AAAA, (uint)Sync << 16 | Sync };
        var body = new List<uint>(header);
        Split([headerChecksum], body);
        Split([dataChecksum], body);
        body.AddRange(dataBlock);

        // The last data bit of the sync word is 1.
        var previous = 1u;
        foreach (var value in body)
        {
            var clocks = ~((value << 1) | (value >> 1) | (previous << 31)) & ~DataBits;
            longs.Add(value | clocks);
            previous = value & 1;
        }

        return longs.ToArray();
    }

    /// <summary>Adds the odd bits of all longs, then the even bits, as data bits of MFM.</summary>
    private static void Split(uint[] values, List<uint> target)
    {
        foreach (var value in values)
            target.Add((value >> 1) & DataBits);
        foreach (var value in values)
            target.Add(value & DataBits);
    }

    private static uint Checksum(IEnumerable<uint> block)
    {
        var sum = 0u;
        foreach (var value in block)
            sum ^= value;
        return sum & DataBits;
    }
}

/// <summary>
/// A 3.5-inch double-density drive with 80 cylinders. The MFM data of each track comes from the ADF when the drive
/// first reads it. A write changes only this copy, not the disk image.
/// </summary>
public sealed class FloppyDrive(bool present)
{
    public const int Cylinders = 80;

    private readonly ushort[]?[] _tracks = new ushort[Cylinders * 2][];
    private byte[]? _image;

    /// <summary>False for a drive that is not connected. Its signals stay high.</summary>
    public bool Present { get; } = present;

    public bool HasDisk => _image != null;
    public bool WriteProtected { get; set; }
    public int Cylinder { get; internal set; }
    public bool MotorOn { get; internal set; }
    public long MotorOnClock { get; internal set; }

    /// <summary>CHNG: the disk was removed or changed. A step pulse with a disk in the drive clears it.</summary>
    public bool DiskChanged { get; internal set; } = true;

    /// <summary>Puts an ADF in the drive: 80 cylinders, 2 heads and 11 sectors of 512 bytes.</summary>
    public void Insert(byte[] image)
    {
        if (image.Length != Cylinders * 2 * AmigaDosMfm.TrackBytes)
            throw new InvalidDataException($"The disk image has {image.Length} bytes. A double-density ADF has 901120.");
        _image = image;
        Array.Clear(_tracks);
        DiskChanged = true;
    }

    public void Eject()
    {
        _image = null;
        Array.Clear(_tracks);
        DiskChanged = true;
    }

    /// <summary>The MFM words of a track, or null if no disk is in the drive.</summary>
    public ushort[]? Track(int track)
    {
        if (_image == null || track is < 0 or >= Cylinders * 2)
            return null;
        return _tracks[track] ??= AmigaDosMfm.EncodeTrack(_image, track);
    }
}

/// <summary>
/// The floppy disk logic of the Amiga: the drive signals on the CIAs and the disk DMA of Paula.
/// </summary>
/// <remarks>
/// <para>
/// CIA-B port B selects the drives and moves the heads: bit 7 MTR, bits 6 to 3 SEL3 to SEL0, bit 2 SIDE, bit 1 DIR and
/// bit 0 STEP, all active low. A drive latches MTR when its SEL goes low. A STEP pulse moves the heads of the selected
/// drives one cylinder: inward when DIR is low. The selected drive drives CIA-A port A: bit 5 RDY, bit 4 TK0, bit 3
/// WPRO and bit 2 CHNG, all active low. RDY comes 0.4 s after the motor starts. With the motor off, RDY gives the ID of
/// the drive. The internal drive DF0 has no ID, so its RDY stays high. Each turn of the disk (200 ms) sends an index
/// pulse to the FLAG input of CIA-B.
/// </para>
/// <para>
/// The disk DMA starts at the second of two writes of DSKLEN with bit 15 set. Bit 14 selects a write, and bits 13 to
/// 0 give the number of words. The transfer starts at the current position of the turning track. With WORDSYNC in
/// ADKCON, a read first waits for the DSKSYNC word, and the words after it go to memory. The transfer ends at once. It
/// sets the DSKBLK interrupt, and DSKSYNC when it found the sync word.
/// </para>
/// </remarks>
public sealed class DiskController
{
    private const byte Step = 0x01, Direction = 0x02, Side = 0x04, Motor = 0x80;
    private const byte Ready = 0x20, TrackZero = 0x10, WriteProtect = 0x08, Change = 0x04;
    private const ushort WordSync = 0x0400;
    private const ushort DiskDma = 0x0010, DmaEnable = 0x0200;
    private const double SpinUpSeconds = 0.4;
    private const double RevolutionSeconds = 0.2;

    private readonly Memory _memory;
    private readonly CustomChips _custom;
    private readonly Cia _ciaA;
    private readonly Cia _ciaB;
    private readonly Beam _beam;
    private byte _portB = 0xFF;
    private long _lastRevolution;
    private bool _lengthArmed;
    private ushort? _pendingLength;

    public DiskController(Memory memory, CustomChips custom, Cia ciaA, Cia ciaB, Beam beam)
    {
        _memory = memory;
        _custom = custom;
        _ciaA = ciaA;
        _ciaB = ciaB;
        _beam = beam;
        ciaB.PortBChanged += PortBChanged;
        custom.DiskLengthWritten += LengthWritten;
        custom.DmaChanged += RunPending;
    }

    /// <summary>DF0 is the internal drive. DF1 to DF3 are not connected.</summary>
    public FloppyDrive[] Drives { get; } = [new(true), new(false), new(false), new(false)];

    private long ClocksPer(double seconds) => (long)(seconds * _beam.ColorClockHz);

    /// <summary>Updates the drive signals and sends the index pulses.</summary>
    public void Update()
    {
        UpdateSignals();
        var selected = Selected();
        var revolution = _beam.ColorClocks / ClocksPer(RevolutionSeconds);
        if (revolution != _lastRevolution && selected is { MotorOn: true, HasDisk: true })
            _ciaB.SignalFlag();
        _lastRevolution = revolution;
    }

    private void PortBChanged(byte levels)
    {
        var old = _portB;
        _portB = levels;
        for (var number = 0; number < Drives.Length; number++)
        {
            var select = (byte)(0x08 << number);
            var drive = Drives[number];
            // A drive latches the motor line when its select line goes low.
            if ((old & select) != 0 && (levels & select) == 0 && drive.Present)
            {
                var on = (levels & Motor) == 0;
                if (on && !drive.MotorOn)
                    drive.MotorOnClock = _beam.ColorClocks;
                drive.MotorOn = on;
            }

            // A step pulse starts when STEP goes low.
            if ((old & Step) != 0 && (levels & Step) == 0 && (levels & select) == 0 && drive.Present)
            {
                var inward = (levels & Direction) == 0;
                drive.Cylinder = Math.Clamp(drive.Cylinder + (inward ? 1 : -1), 0, FloppyDrive.Cylinders + 3);
                if (drive.HasDisk)
                    drive.DiskChanged = false;
            }
        }

        UpdateSignals();
    }

    /// <summary>The first drive whose select line is low, or null.</summary>
    private FloppyDrive? Selected()
    {
        for (var number = 0; number < Drives.Length; number++)
        {
            if ((_portB & (0x08 << number)) == 0 && Drives[number].Present)
                return Drives[number];
        }

        return null;
    }

    private void UpdateSignals()
    {
        byte levels = Ready | TrackZero | WriteProtect | Change;
        if (Selected() is { } drive)
        {
            if (drive.MotorOn && _beam.ColorClocks - drive.MotorOnClock >= ClocksPer(SpinUpSeconds))
                levels &= unchecked((byte)~Ready);
            if (drive.Cylinder == 0)
                levels &= unchecked((byte)~TrackZero);
            if (drive.HasDisk && drive.WriteProtected)
                levels &= unchecked((byte)~WriteProtect);
            if (drive.DiskChanged)
                levels &= unchecked((byte)~Change);
        }

        _ciaA.SetInputPinsA(Ready | TrackZero | WriteProtect | Change, levels);
    }

    private void LengthWritten(ushort value)
    {
        if ((value & 0x8000) == 0)
        {
            _lengthArmed = false;
            _pendingLength = null;
            return;
        }

        if (!_lengthArmed)
        {
            _lengthArmed = true;
            return;
        }

        _lengthArmed = false;
        _pendingLength = value;
        RunPending();
    }

    private void RunPending()
    {
        const ushort enabled = DmaEnable | DiskDma;
        if (_pendingLength is not { } length || (_custom.Dmacon & enabled) != enabled)
            return;
        _pendingLength = null;

        var drive = Selected();
        var head = (_portB & Side) != 0 ? 0 : 1;
        var track = drive is { MotorOn: true } ? drive.Track(drive.Cylinder * 2 + head) : null;
        if (track == null)
            return; // No disk turns under the head, so the transfer never ends.

        var words = length & 0x3FFF;
        var pointer = (uint)(_custom[DiskRegister.Dskpt] << 16 | _custom[DiskRegister.Dskpt + 2]) & 0x1F_FFFE;
        var position = (int)(_beam.ColorClocks / (ClocksPer(RevolutionSeconds) / AmigaDosMfm.TrackWords)
                             % AmigaDosMfm.TrackWords);

        if ((length & 0x4000) != 0)
        {
            for (var i = 0; i < words; i++)
                track[(position + i) % track.Length] = _memory.Read16(pointer + (uint)i * 2);
        }
        else
        {
            if ((_custom.Adkcon & WordSync) != 0)
            {
                var sync = _custom[DiskRegister.Dsksync];
                var found = Enumerable.Range(0, track.Length).Select(i => (position + i) % track.Length)
                    .Where(i => track[i] == sync).Select(i => (int?)i).FirstOrDefault();
                if (found == null)
                    return;
                position = found.Value + 1;
                _custom.RequestInterrupt(InterruptBit.DiskSync);
            }

            for (var i = 0; i < words; i++)
                _memory.Write16(pointer + (uint)i * 2, track[(position + i) % track.Length]);
        }

        pointer += (uint)words * 2;
        _custom.Write(DiskRegister.Dskpt, (ushort)(pointer >> 16));
        _custom.Write(DiskRegister.Dskpt + 2, (ushort)pointer);
        _custom.RequestInterrupt(InterruptBit.DiskBlock);
    }
}

/// <summary>The offsets of the disk registers from $DFF000.</summary>
public static class DiskRegister
{
    public const int Dskpt = 0x020;
    public const int Dsklen = 0x024;
    public const int Dsksync = 0x07E;
}
