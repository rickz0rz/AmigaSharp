using System.Text;

namespace AmigaSharp.Runtime.Dos;

/// <summary>The error codes that IoErr returns (dos/dos.h).</summary>
public static class DosError
{
    public const int NoFreeStore = 103;
    public const int ObjectInUse = 202;
    public const int ObjectExists = 203;
    public const int DirectoryNotFound = 204;
    public const int ObjectNotFound = 205;
    public const int BadStreamName = 206;
    public const int ActionNotKnown = 209;
    public const int InvalidComponentName = 210;
    public const int ObjectWrongType = 212;
    public const int DiskNotValidated = 213;
    public const int WriteProtected = 214;
    public const int DirectoryNotEmpty = 216;
    public const int DeviceNotMounted = 218;
    public const int SeekError = 219;
    public const int DiskFull = 221;
    public const int DeleteProtected = 222;
    public const int NoMoreEntries = 232;
}

/// <summary>The modes of Open and Lock (dos/dos.h).</summary>
public static class DosMode
{
    public const int ReadWrite = 1004;
    public const int OldFile = 1005;
    public const int NewFile = 1006;

    public const int SharedLock = -2;
    public const int ExclusiveLock = -1;

    public const int OffsetBeginning = -1;
    public const int OffsetCurrent = 0;
    public const int OffsetEnd = 1;
}

/// <summary>
/// Maps AmigaDOS files to files on the host. A volume or an assign (for example "SYS:" or "DF0:") is a host
/// directory. Names do not have to match in case, as on the Amiga. The console names ("*", "CON:", "RAW:") use the
/// input and output streams of the runtime, and "NIL:" discards all data.
/// </summary>
/// <remarks>
/// A file handle and a lock are BPTRs to real <c>FileHandle</c> and <c>FileLock</c> structures in memory. The
/// runtime keeps the host objects in tables that use the BPTR as the key.
/// </remarks>
public sealed class FileSystem
{
    private const uint FileHandleSize = 44;
    private const uint FileLockSize = 20;

    // FileHandle offsets (dos/dosextens.h).
    private const uint FileHandleInteractive = 4;
    private const uint FileHandleArg1 = 36;

    // FileLock offsets.
    private const uint FileLockKey = 4;
    private const uint FileLockAccess = 8;

    private readonly Core _core;
    private readonly Dictionary<string, string> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _assigns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, OpenFile> _files = new();
    private readonly Dictionary<uint, Lock> _locks = new();
    private readonly HashSet<string> _resolving = new(StringComparer.OrdinalIgnoreCase);
    private int _nextKey = 1;

    private sealed record OpenFile(Stream? Stream, bool IsConsole, string Name);

    private sealed record Lock(string HostPath, string AmigaPath, int Access);

    public FileSystem(Core core, string rootDirectory)
    {
        _core = core;
        AddVolume("SYS", rootDirectory);
        // The standard assigns of AmigaDOS point to directories of SYS:.
        foreach (var (assign, directory) in new[] { ("C", "C"), ("S", "S"), ("L", "L"), ("LIBS", "Libs"), ("DEVS", "Devs"), ("FONTS", "Fonts") })
            AddAssign(assign, "SYS:" + directory);
    }

    /// <summary>The volume of names that have no volume and of the first current directory.</summary>
    public string DefaultVolume { get; private set; } = "SYS";

    /// <summary>Makes a volume on a host directory, for example <c>AddVolume("DF0", "/path/to/disk")</c>.</summary>
    public void AddVolume(string name, string hostDirectory)
    {
        name = name.TrimEnd(':');
        _assigns.Remove(name);
        _volumes[name] = Path.GetFullPath(hostDirectory);
    }

    /// <summary>
    /// Makes an assign to an AmigaDOS directory, as the Assign command does, for example
    /// <c>AddAssign("FONTS", "SYS:Fonts")</c>. The runtime finds the directory each time that a name uses the assign,
    /// so the directory can have another case on the host, and it does not have to exist yet.
    /// </summary>
    public void AddAssign(string name, string directory)
    {
        name = name.TrimEnd(':');
        _volumes.Remove(name);
        _assigns[name] = directory;
    }

    /// <summary>The host directory of the lock, or of the default volume if the lock is 0.</summary>
    public string HostPathOf(uint lockBptr) =>
        lockBptr != 0 && _locks.TryGetValue(lockBptr, out var found) ? found.HostPath : _volumes[DefaultVolume];

    /// <summary>
    /// Opens a file. Returns the BPTR of the file handle, or 0 and an error code.
    /// </summary>
    public (uint Handle, int Error) Open(string name, int mode, uint currentDirectory)
    {
        var upper = name.ToUpperInvariant();
        if (name == "*" || upper.StartsWith("CON:", StringComparison.Ordinal) || upper.StartsWith("RAW:", StringComparison.Ordinal))
            return (CreateHandle(new OpenFile(null, true, name), interactive: true), 0);
        if (upper == "NIL:")
            return (CreateHandle(new OpenFile(Stream.Null, false, name), interactive: false), 0);

        var (hostPath, error) = Resolve(name, currentDirectory, mustExist: mode == DosMode.OldFile);
        if (hostPath == null)
            return (0, error);
        if (Directory.Exists(hostPath))
            return (0, DosError.ObjectWrongType);

        try
        {
            var stream = mode switch
            {
                DosMode.NewFile => new FileStream(hostPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite),
                DosMode.ReadWrite => new FileStream(hostPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite),
                _ => OpenExisting(hostPath),
            };
            return (CreateHandle(new OpenFile(stream, false, name), interactive: false), 0);
        }
        catch (FileNotFoundException)
        {
            return (0, DosError.ObjectNotFound);
        }
        catch (DirectoryNotFoundException)
        {
            return (0, DosError.DirectoryNotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, DosError.WriteProtected);
        }
        catch (IOException)
        {
            return (0, DosError.ObjectInUse);
        }
    }

    /// <summary>Makes a console file handle. The main process uses one for its input and one for its output.</summary>
    public uint OpenConsole() => CreateHandle(new OpenFile(null, true, "*"), interactive: true);

    public bool Close(uint handle)
    {
        if (!_files.Remove(handle, out var file))
            return false;
        file.Stream?.Dispose();
        _core.FreeSystem(handle << 2, FileHandleSize);
        return true;
    }

    public bool IsInteractive(uint handle) => _files.TryGetValue(handle, out var file) && file.IsConsole;

    /// <summary>Read: returns the number of bytes, 0 at the end of the file, or -1 for an error.</summary>
    public int Read(uint handle, uint buffer, int length)
    {
        if (!_files.TryGetValue(handle, out var file) || length < 0)
            return -1;

        var stream = file.IsConsole ? _core.Input : file.Stream!;
        var bytes = new byte[length];
        var count = stream.Read(bytes, 0, length);
        _core.Memory.WriteBytes(buffer, bytes.AsSpan(0, count));
        return count;
    }

    /// <summary>Write: returns the number of bytes, or -1 for an error.</summary>
    public int Write(uint handle, uint buffer, int length)
    {
        if (!_files.TryGetValue(handle, out var file) || length < 0)
            return -1;

        var stream = file.IsConsole ? _core.Output : file.Stream!;
        try
        {
            stream.Write(_core.Memory.ReadBytes(buffer, length));
            stream.Flush();
            return length;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    /// <summary>Seek: returns the old position, or -1 for an error.</summary>
    public int Seek(uint handle, int position, int mode)
    {
        if (!_files.TryGetValue(handle, out var file) || file.Stream is not { CanSeek: true } stream)
            return -1;

        var old = stream.Position;
        var origin = mode switch
        {
            DosMode.OffsetBeginning => SeekOrigin.Begin,
            DosMode.OffsetEnd => SeekOrigin.End,
            _ => SeekOrigin.Current,
        };
        var target = origin switch
        {
            SeekOrigin.Begin => position,
            SeekOrigin.End => stream.Length + position,
            _ => old + position,
        };
        if (target < 0 || target > stream.Length)
            return -1;
        stream.Position = target;
        return (int)old;
    }

    /// <summary>Lock: returns the BPTR of the lock, or 0 and an error code.</summary>
    public (uint Lock, int Error) CreateLock(string name, int access, uint currentDirectory)
    {
        var (hostPath, error) = Resolve(name, currentDirectory, mustExist: true);
        if (hostPath == null)
            return (0, error);

        var address = _core.AllocateSystem(FileLockSize);
        var bptr = address >> 2;
        _core.Memory.Write32(address + FileLockKey, (uint)_nextKey++);
        _core.Memory.Write32(address + FileLockAccess, (uint)access);
        _locks[bptr] = new Lock(hostPath, name, access);
        return (bptr, 0);
    }

    /// <summary>DupLock: returns a new lock on the same object, or 0.</summary>
    public uint DuplicateLock(uint lockBptr)
    {
        if (lockBptr == 0)
            return 0;
        if (!_locks.TryGetValue(lockBptr, out var existing))
            return 0;

        var address = _core.AllocateSystem(FileLockSize);
        var bptr = address >> 2;
        _core.Memory.Write32(address + FileLockKey, (uint)_nextKey++);
        _core.Memory.Write32(address + FileLockAccess, (uint)existing.Access);
        _locks[bptr] = existing;
        return bptr;
    }

    public void Unlock(uint lockBptr)
    {
        if (lockBptr != 0 && _locks.Remove(lockBptr))
            _core.FreeSystem(lockBptr << 2, FileLockSize);
    }

    public bool IsDirectory(uint lockBptr) => Directory.Exists(HostPathOf(lockBptr));

    /// <summary>DeleteFile: returns 0 for success or an error code.</summary>
    public int Delete(string name, uint currentDirectory)
    {
        var (hostPath, error) = Resolve(name, currentDirectory, mustExist: true);
        if (hostPath == null)
            return error;

        try
        {
            if (Directory.Exists(hostPath))
            {
                if (Directory.EnumerateFileSystemEntries(hostPath).Any())
                    return DosError.DirectoryNotEmpty;
                Directory.Delete(hostPath);
            }
            else
            {
                File.Delete(hostPath);
            }

            return 0;
        }
        catch (IOException)
        {
            return DosError.ObjectInUse;
        }
        catch (UnauthorizedAccessException)
        {
            return DosError.DeleteProtected;
        }
    }

    /// <summary>
    /// Finds the host path of an AmigaDOS name. Returns the path, or null and an error code. If the object does not
    /// have to exist, the directory must exist, and the last name keeps its case.
    /// </summary>
    public (string? Path, int Error) Resolve(string name, uint currentDirectory, bool mustExist)
    {
        string directory;
        var rest = name;
        var colon = name.IndexOf(':');
        if (colon >= 0)
        {
            var volume = name[..colon];
            rest = name[(colon + 1)..];
            if (volume.Length == 0)
            {
                // ":" is the root of the volume of the current directory.
                directory = RootOf(HostPathOf(currentDirectory));
            }
            else if (_assigns.TryGetValue(volume, out var target))
            {
                // An assign can point to another assign, but not to itself.
                if (_resolving.Contains(volume))
                    return (null, DosError.DeviceNotMounted);
                _resolving.Add(volume);
                try
                {
                    var (assigned, error) = Resolve(target, currentDirectory, mustExist: true);
                    if (assigned == null)
                        return (null, error == DosError.ObjectNotFound ? DosError.DirectoryNotFound : error);
                    directory = assigned;
                }
                finally
                {
                    _resolving.Remove(volume);
                }
            }
            else if (!_volumes.TryGetValue(volume, out directory!))
            {
                return (null, DosError.DeviceNotMounted);
            }
        }
        else
        {
            directory = HostPathOf(currentDirectory);
        }

        var parts = rest.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var isLast = i == parts.Length - 1;
            if (part.Length == 0)
            {
                // An empty name between slashes, or a leading slash, means the parent directory.
                if (isLast)
                    break;
                directory = Path.GetDirectoryName(directory) ?? directory;
                continue;
            }

            var match = FindEntry(directory, part);
            if (match == null)
            {
                if (!isLast)
                    return (null, DosError.DirectoryNotFound);
                return mustExist ? (null, DosError.ObjectNotFound) : (Path.Combine(directory, part), 0);
            }

            directory = match;
        }

        return (directory, 0);
    }

    private string RootOf(string hostPath)
    {
        var volume = _volumes.Values
            .Where(root => hostPath.StartsWith(root, StringComparison.Ordinal))
            .MaxBy(root => root.Length);
        return volume ?? _volumes[DefaultVolume];
    }

    /// <summary>Finds a file or a directory. The case of the name does not have to match.</summary>
    private static string? FindEntry(string directory, string name)
    {
        var exact = Path.Combine(directory, name);
        if (File.Exists(exact) || Directory.Exists(exact))
            return exact;
        if (!Directory.Exists(directory))
            return null;
        return Directory.EnumerateFileSystemEntries(directory)
            .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
    }

    private static FileStream OpenExisting(string hostPath)
    {
        try
        {
            return new FileStream(hostPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (UnauthorizedAccessException)
        {
            return new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
    }

    private uint CreateHandle(OpenFile file, bool interactive)
    {
        var address = _core.AllocateSystem(FileHandleSize);
        var bptr = address >> 2;
        // fh_Port is not zero for an interactive handle. fh_Arg1 holds the key of the host object.
        _core.Memory.Write32(address + FileHandleInteractive, interactive ? 0xFFFF_FFFFu : 0);
        _core.Memory.Write32(address + FileHandleArg1, (uint)_nextKey++);
        _files[bptr] = file;
        return bptr;
    }

    /// <summary>Reads a BSTR: a BPTR to a length byte and the characters.</summary>
    public static string ReadBstr(Memory memory, uint bstr)
    {
        if (bstr == 0)
            return "";
        var address = bstr << 2;
        var length = memory.Read8(address);
        return Encoding.Latin1.GetString(memory.ReadBytes(address + 1, length));
    }
}
