using System.Text;
using AmigaSharp.Runtime.Dos;
using AmigaSharp.Runtime.Exec;

namespace AmigaSharp.Tests.Libraries;

public sealed class DosLibraryTests : IDisposable
{
    private const short Open = -30;
    private const short Close = -36;
    private const short Read = -42;
    private const short Write = -48;
    private const short Output = -60;
    private const short Seek = -66;
    private const short DeleteFile = -72;
    private const short Rename = -78;
    private const short Lock = -84;
    private const short UnLock = -90;
    private const short CurrentDir = -126;
    private const short IoErr = -132;
    private const short CreateProc = -138;
    private const short DateStamp = -192;
    private const short Delay = -198;
    private const short IsInteractive = -216;
    private const short PutStr = -948;
    private const short VPrintf = -954;

    private readonly LibraryHarness _harness = new();
    private uint Dos => _harness.DosBase;

    [Fact]
    public void WriteSeekRead_UsesAHostFile()
    {
        var file = _harness.Call(Dos, Open, ("D1", _harness.String("notes.txt")), ("D2", DosMode.NewFile));
        var text = _harness.String("abcdef");
        Assert.Equal(6u, _harness.Call(Dos, Write, ("D1", file), ("D2", text), ("D3", 6)));

        Assert.Equal(6u, _harness.Call(Dos, Seek, ("D1", file), ("D2", 2), ("D3", unchecked((uint)DosMode.OffsetBeginning))));
        var buffer = _harness.Core.AllocateSystem(16);
        Assert.Equal(4u, _harness.Call(Dos, Read, ("D1", file), ("D2", buffer), ("D3", 16)));
        _harness.Call(Dos, Close, ("D1", file));

        Assert.Equal("cdef", _harness.Memory.ReadCString(buffer));
        Assert.Equal("abcdef", File.ReadAllText(Path.Combine(_harness.Root, "notes.txt")));
    }

    [Fact]
    public void Open_IgnoresTheCaseOfNames_AndKnowsTheVolume()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Root, "Data"));
        File.WriteAllText(Path.Combine(_harness.Root, "Data", "Config.INI"), "x");

        var file = _harness.Call(Dos, Open, ("D1", _harness.String("sys:data/config.ini")), ("D2", DosMode.OldFile));

        Assert.NotEqual(0u, file);
        _harness.Call(Dos, Close, ("D1", file));
    }

    [Fact]
    public void StandardAssign_FindsTheDirectoryInAnyCase()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Root, "fonts"));
        File.WriteAllText(Path.Combine(_harness.Root, "fonts", "a.font"), "x");

        var file = _harness.Call(Dos, Open, ("D1", _harness.String("FONTS:a.font")), ("D2", DosMode.OldFile));

        Assert.NotEqual(0u, file);
        _harness.Call(Dos, Close, ("D1", file));
    }

    [Fact]
    public void Assign_ToAnotherVolume_UsesThatVolume()
    {
        _harness.Core.FileSystem.AddAssign("DF0", "SYS:");
        File.WriteAllText(Path.Combine(_harness.Root, "config.dat"), "x");

        var file = _harness.Call(Dos, Open, ("D1", _harness.String("df0:config.dat")), ("D2", DosMode.OldFile));

        Assert.NotEqual(0u, file);
        _harness.Call(Dos, Close, ("D1", file));
    }

    [Fact]
    public void Open_MissingFile_SetsIoErr()
    {
        var file = _harness.Call(Dos, Open, ("D1", _harness.String("missing")), ("D2", DosMode.OldFile));

        Assert.Equal(0u, file);
        Assert.Equal((uint)DosError.ObjectNotFound, _harness.Call(Dos, IoErr));
    }

    [Fact]
    public void Open_UnknownVolume_SetsIoErr()
    {
        _harness.Call(Dos, Open, ("D1", _harness.String("DF3:file")), ("D2", DosMode.OldFile));

        Assert.Equal((uint)DosError.DeviceNotMounted, _harness.Call(Dos, IoErr));
    }

    [Fact]
    public void CurrentDir_ChangesTheDirectoryOfRelativeNames()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Root, "work"));
        File.WriteAllText(Path.Combine(_harness.Root, "work", "a.txt"), "x");
        var directory = _harness.Call(Dos, Lock, ("D1", _harness.String("work")), ("D2", unchecked((uint)DosMode.SharedLock)));

        var old = _harness.Call(Dos, CurrentDir, ("D1", directory));

        Assert.Equal(0u, old);
        var relative = _harness.Call(Dos, Open, ("D1", _harness.String("a.txt")), ("D2", DosMode.OldFile));
        var fromParent = _harness.Call(Dos, Open, ("D1", _harness.String("/work/a.txt")), ("D2", DosMode.OldFile));
        Assert.NotEqual(0u, relative);
        Assert.NotEqual(0u, fromParent);
        _harness.Call(Dos, Close, ("D1", relative));
        _harness.Call(Dos, Close, ("D1", fromParent));
        _harness.Call(Dos, CurrentDir, ("D1", old));
        _harness.Call(Dos, UnLock, ("D1", directory));
    }

    [Fact]
    public void DeleteAndRename_ChangeHostFiles()
    {
        File.WriteAllText(Path.Combine(_harness.Root, "old.txt"), "x");

        Assert.Equal(unchecked((uint)-1), _harness.Call(Dos, Rename, ("D1", _harness.String("old.txt")), ("D2", _harness.String("new.txt"))));
        Assert.True(File.Exists(Path.Combine(_harness.Root, "new.txt")));
        Assert.Equal(unchecked((uint)-1), _harness.Call(Dos, DeleteFile, ("D1", _harness.String("NEW.TXT"))));
        Assert.False(File.Exists(Path.Combine(_harness.Root, "new.txt")));
    }

    [Fact]
    public void CreateProc_FreesTheMemoryOfTheProcessWhenItEnds()
    {
        // A segment is the BPTR to the next segment, and then the code: RTS.
        var segment = _harness.Core.AllocateSystem([0, 0, 0, 0, 0x4E, 0x75]);
        var name = _harness.String("child");
        var free = _harness.Core.Allocator.Available(MemoryFlags.Any);

        for (var i = 0; i < 3; i++)
        {
            _harness.Call(Dos, CreateProc, ("D1", name), ("D2", 0), ("D3", segment >> 2), ("D4", 8192));
            // The child runs while the main task waits.
            while (_harness.Core.Scheduler.Tasks.Count() > 1)
                _harness.Call(Dos, Delay, ("D1", 1));
        }

        Assert.Equal(free, _harness.Core.Allocator.Available(MemoryFlags.Any));
    }

    [Fact]
    public void Output_IsTheConsole()
    {
        var output = _harness.Call(Dos, Output);

        Assert.Equal(unchecked((uint)-1), _harness.Call(Dos, IsInteractive, ("D1", output)));
        _harness.Call(Dos, PutStr, ("D1", _harness.String("line\n")));
        Assert.Equal("line\n", _harness.OutputText);
    }

    [Fact]
    public void VPrintf_FormatsWithRawDoFmtRules()
    {
        var arguments = _harness.Core.AllocateSystem(8);
        _harness.Memory.Write32(arguments, 1234567);
        _harness.Memory.Write32(arguments + 4, _harness.String("done"));

        var count = _harness.Call(Dos, VPrintf, ("D1", _harness.String("%ld %s\n")), ("D2", arguments));

        Assert.Equal("1234567 done\n", _harness.OutputText);
        Assert.Equal(13u, count);
    }

    [Fact]
    public void DateStamp_CountsFrom1978()
    {
        var stamp = _harness.Core.AllocateSystem(12);

        _harness.Call(Dos, DateStamp, ("D1", stamp));

        var days = _harness.Memory.Read32(stamp);
        Assert.Equal((DateTime.Now.Date - new DateTime(1978, 1, 1)).Days, (int)days);
        Assert.InRange(_harness.Memory.Read32(stamp + 4), 0u, 1439u);
    }

    [Fact]
    public void Process_HasConsoleStreams()
    {
        var process = _harness.Core.MainProcess;

        Assert.Equal(_harness.Call(Dos, Output), _harness.Memory.Read32(process + ProcessOffsets.OutputStream));
    }

    public void Dispose() => _harness.Dispose();
}
