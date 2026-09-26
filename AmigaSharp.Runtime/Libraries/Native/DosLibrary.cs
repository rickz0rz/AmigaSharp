using AmigaSharp.Runtime.Dos;
using AmigaSharp.Runtime.Exec;

namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// dos.library. The files are host files: see <see cref="FileSystem"/>. A function that fails sets the error code
/// that IoErr returns.
/// </summary>
public class DosLibrary(Core core) : AbstractLibrary
{
    private const int DosTrue = -1;
    private const int DosFalse = 0;

    // InfoData offsets and values (dos/dos.h).
    private const uint InfoDiskState = 8;
    private const uint InfoNumberOfBlocks = 12;
    private const uint InfoBlocksUsed = 16;
    private const uint InfoBytesPerBlock = 20;
    private const uint InfoDiskType = 24;
    private const uint InfoInUse = 32;
    private const uint DiskStateValidated = 82;
    private const uint DiskTypeDos = 0x444F_5300;

    private static readonly DateTime AmigaEpoch = new(1978, 1, 1, 0, 0, 0, DateTimeKind.Local);

    private readonly Memory _memory = core.Memory;
    private readonly FileSystem _files = core.FileSystem;
    private readonly Shell _shell = new(core);

    public override string Name => "dos.library";
    public override ushort Version => 40;
    public override ushort Revision => 3;
    public override short LowestOffset => -984;

    private uint Process => _memory.Read32(core.ExecBase + ExecBaseOffsets.ThisTask);

    private uint CurrentDirectory => _memory.Read32(Process + ProcessOffsets.CurrentDir);

    // file = Open(name, accessMode)
    // D0          D1    D2
    [LibraryFunctionOffset(-30)]
    public uint Open([D1] uint name, [D2] int mode)
    {
        var (handle, error) = _files.Open(_memory.ReadCString(name), mode, CurrentDirectory);
        SetError(error);
        return handle;
    }

    // success = Close(file)
    // D0              D1
    [LibraryFunctionOffset(-36)]
    public int Close([D1] uint file) => file == 0 || _files.Close(file) ? DosTrue : DosFalse;

    // actualLength = Read(file, buffer, length)
    // D0                  D1    D2      D3
    [LibraryFunctionOffset(-42)]
    public int Read([D1] uint file, [D2] uint buffer, [D3] int length) => _files.Read(file, buffer, length);

    // returnedLength = Write(file, buffer, length)
    // D0                     D1    D2      D3
    [LibraryFunctionOffset(-48)]
    public int Write([D1] uint file, [D2] uint buffer, [D3] int length) => _files.Write(file, buffer, length);

    // file = Input()
    // D0
    [LibraryFunctionOffset(-54)]
    public uint Input() => _memory.Read32(Process + ProcessOffsets.InputStream);

    // file = Output()
    // D0
    [LibraryFunctionOffset(-60)]
    public uint Output() => _memory.Read32(Process + ProcessOffsets.OutputStream);

    // oldPosition = Seek(file, position, mode)
    // D0                 D1    D2        D3
    [LibraryFunctionOffset(-66)]
    public int Seek([D1] uint file, [D2] int position, [D3] int mode)
    {
        var old = _files.Seek(file, position, mode);
        if (old < 0)
            SetError(DosError.SeekError);
        return old;
    }

    // success = DeleteFile(name)
    // D0                   D1
    [LibraryFunctionOffset(-72)]
    public int DeleteFile([D1] uint name)
    {
        var error = _files.Delete(_memory.ReadCString(name), CurrentDirectory);
        SetError(error);
        return error == 0 ? DosTrue : DosFalse;
    }

    // success = Rename(oldName, newName)
    // D0               D1       D2
    [LibraryFunctionOffset(-78)]
    public int Rename([D1] uint oldName, [D2] uint newName)
    {
        var (source, error) = _files.Resolve(_memory.ReadCString(oldName), CurrentDirectory, mustExist: true);
        var (target, targetError) = _files.Resolve(_memory.ReadCString(newName), CurrentDirectory, mustExist: false);
        if (source == null || target == null)
        {
            SetError(source == null ? error : targetError);
            return DosFalse;
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            SetError(DosError.ObjectExists);
            return DosFalse;
        }

        if (Directory.Exists(source))
            Directory.Move(source, target);
        else
            File.Move(source, target);
        return DosTrue;
    }

    // lock = Lock(name, accessMode)
    // D0          D1    D2
    [LibraryFunctionOffset(-84)]
    public uint Lock([D1] uint name, [D2] int mode)
    {
        var (lockBptr, error) = _files.CreateLock(_memory.ReadCString(name), mode, CurrentDirectory);
        SetError(error);
        return lockBptr;
    }

    // UnLock(lock)
    //        D1
    [LibraryFunctionOffset(-90)]
    public void UnLock([D1] uint lockBptr) => _files.Unlock(lockBptr);

    // lock = DupLock(lock)
    // D0             D1
    [LibraryFunctionOffset(-96)]
    public uint DupLock([D1] uint lockBptr) => _files.DuplicateLock(lockBptr);

    // success = Info(lock, parameterBlock)
    // D0             D1    D2
    [LibraryFunctionOffset(-114)]
    public int Info([D1] uint lockBptr, [D2] uint infoData)
    {
        var drive = new DriveInfo(Path.GetPathRoot(_files.HostPathOf(lockBptr))!);
        const long blockSize = 512;
        var blocks = Math.Min(drive.TotalSize / blockSize, int.MaxValue);
        var free = Math.Min(drive.AvailableFreeSpace / blockSize, blocks);
        _memory.Write32(infoData + InfoDiskState, DiskStateValidated);
        _memory.Write32(infoData + InfoNumberOfBlocks, (uint)blocks);
        _memory.Write32(infoData + InfoBlocksUsed, (uint)(blocks - free));
        _memory.Write32(infoData + InfoBytesPerBlock, (uint)blockSize);
        _memory.Write32(infoData + InfoDiskType, DiskTypeDos);
        _memory.Write32(infoData + InfoInUse, 0);
        return DosTrue;
    }

    // oldLock = CurrentDir(lock)
    // D0                   D1
    [LibraryFunctionOffset(-126)]
    public uint CurrentDir([D1] uint lockBptr)
    {
        var old = CurrentDirectory;
        _memory.Write32(Process + ProcessOffsets.CurrentDir, lockBptr);
        return old;
    }

    // error = IoErr()
    // D0
    [LibraryFunctionOffset(-132)]
    public int IoErr() => (int)_memory.Read32(Process + ProcessOffsets.Result2);

    // process = CreateProc(name, pri, segList, stackSize)
    // D0                   D1    D2   D3       D4
    [LibraryFunctionOffset(-138)]
    // The process runs the code of the first segment of the segment list: 4 bytes after the start, because the first
    // long of a segment is the BPTR to the next segment. Returns the message port of the process.
    public uint CreateProc([D1] uint name, [D2] int priority, [D3] uint segList, [D4] uint stackSize)
    {
        var parent = Process;
        var process = core.CreateProcess(_memory.ReadCString(name), (sbyte)priority, stackSize);
        _memory.Write32(process + ProcessOffsets.SegList, segList);
        foreach (var field in new[] { ProcessOffsets.CurrentDir, ProcessOffsets.InputStream, ProcessOffsets.OutputStream, ProcessOffsets.WindowPtr })
            _memory.Write32(process + field, _memory.Read32(parent + field));

        var entry = (segList << 2) + 4;
        core.Scheduler.Start(process, entry, _memory.Read32(process + TaskOffsets.StackUpper));
        return process + ProcessOffsets.MsgPort;
    }

    // ds = DateStamp(ds)
    // D0             D1
    [LibraryFunctionOffset(-192)]
    public uint DateStamp([D1] uint dateStamp)
    {
        var elapsed = core.Now - AmigaEpoch;
        _memory.Write32(dateStamp, (uint)elapsed.Days);
        _memory.Write32(dateStamp + 4, (uint)(elapsed.Hours * 60 + elapsed.Minutes));
        _memory.Write32(dateStamp + 8, (uint)(elapsed.Seconds * 50 + elapsed.Milliseconds / 20));
        return dateStamp;
    }

    // Delay(ticks)
    //       D1
    // A tick is 1/50 second.
    [LibraryFunctionOffset(-198)]
    public void Delay([D1] int ticks)
    {
        // Other tasks run during the delay.
        if (ticks > 0)
            core.Scheduler.Delay(TimeSpan.FromSeconds(ticks / 50.0));
    }

    // status = IsInteractive(file)
    // D0                     D1
    [LibraryFunctionOffset(-216)]
    public int IsInteractive([D1] uint file) => _files.IsInteractive(file) ? DosTrue : DosFalse;

    // success = Execute(commandString, input, output)
    // D0                D1             D2     D3
    [LibraryFunctionOffset(-222)]
    // The shell of the runtime runs the command. Returns DOSTRUE if the command ran, also if it failed.
    public int Execute([D1] uint command, [D2] uint input, [D3] uint output) =>
        RunCommand(_memory.ReadCString(command), output) == Shell.UnknownCommand ? DosFalse : DosTrue;

    // char = FPutC(fh, char)
    // D0           D1  D2
    [LibraryFunctionOffset(-312)]
    public int FPutC([D1] uint file, [D2] uint character)
    {
        var buffer = core.AllocateSystem([(byte)character]);
        var written = _files.Write(file, buffer, 1);
        core.FreeSystem(buffer, 1);
        return written == 1 ? (int)(character & 0xFF) : -1;
    }

    // error = FPuts(fh, str)
    // D0            D1  D2
    [LibraryFunctionOffset(-342)]
    public int FPuts([D1] uint file, [D2] uint text) => WriteString(file, _memory.ReadCStringBytes(text)) ? 0 : -1;

    // count = VFPrintf(fh, fmt, argv)
    // D0               D1  D2   D3
    [LibraryFunctionOffset(-354)]
    public int VFPrintf([D1] uint file, [D2] uint format, [D3] uint arguments)
    {
        var (text, _) = RawDoFormat.Format(_memory, format, arguments);
        return WriteString(file, text) ? text.Length : -1;
    }

    // success = Flush(fh)
    // D0              D1
    // The runtime does not buffer, so Flush has nothing to do.
    [LibraryFunctionOffset(-360)]
    public int Flush([D1] uint file) => DosTrue;

    // success = SetIoErr(result)
    // D0                 D1
    [LibraryFunctionOffset(-462)]
    public int SetIoErr([D1] int result)
    {
        var old = IoErr();
        SetError(result);
        return old;
    }

    // error = SystemTagList(command, tags)
    // D0                    D1       D2
    [LibraryFunctionOffset(-606)]
    // Returns the return code of the command, or -1 if the command cannot run.
    public int SystemTagList([D1] uint command, [D2] uint tags) => RunCommand(_memory.ReadCString(command), 0);

    // https://d0.se/autodocs/dos.library/PutStr
    //
    // error = PutStr(str)
    // D0             D1
    // LONG PutStr(STRPTR)
    //
    // INPUTS
    // str   - Null-terminated string to be written to default output
    //
    // RESULT
    // error - 0 for success, -1 for any error.  NOTE: this is opposite
    // most Dos function returns!
    [LibraryFunctionOffset(-948)]
    public int PutStr([D1] uint text) => WriteString(Output(), _memory.ReadCStringBytes(text)) ? 0 : -1;

    // count = VPrintf(fmt, argv)
    // D0              D1   D2
    [LibraryFunctionOffset(-954)]
    public int VPrintf([D1] uint format, [D2] uint arguments) => VFPrintf(Output(), format, arguments);

    /// <summary>Runs a command in the shell of the runtime. The output goes to the file handle, or to the console.</summary>
    private int RunCommand(string commandLine, uint output)
    {
        using var stream = new MemoryStream();
        var result = _shell.Run(commandLine, CurrentDirectory, stream);
        if (result == Shell.UnknownCommand)
        {
            core.Log.WriteLine($"Execute(\"{commandLine}\"): the runtime has no such command.");
            SetError(DosError.ObjectNotFound);
            return -1;
        }

        var text = stream.ToArray();
        if (output != 0)
            WriteString(output, text);
        else if (text.Length > 0)
            WriteString(Output(), text);
        return result;
    }

    private bool WriteString(uint file, byte[] text)
    {
        if (text.Length == 0)
            return true;
        var buffer = core.AllocateSystem(text);
        var written = _files.Write(file, buffer, text.Length);
        core.FreeSystem(buffer, (uint)text.Length);
        return written == text.Length;
    }

    /// <summary>Sets the code that IoErr returns.</summary>
    private void SetError(int error) => _memory.Write32(Process + ProcessOffsets.Result2, (uint)error);
}
