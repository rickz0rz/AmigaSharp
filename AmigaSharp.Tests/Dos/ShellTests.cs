using AmigaSharp.Tests.Libraries;

namespace AmigaSharp.Tests.Dos;

/// <summary>Tests of the shell of the runtime, through dos Execute and SystemTagList.</summary>
public sealed class ShellTests : IDisposable
{
    private const short Execute = -222;
    private const short SystemTagList = -606;

    private readonly LibraryHarness _harness = new();

    [Fact]
    public void Copy_CopiesAFileToADirectory()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Root, "C"));
        File.WriteAllText(Path.Combine(_harness.Root, "C", "Assign"), "command");
        Directory.CreateDirectory(Path.Combine(_harness.Root, "ram"));
        _harness.Core.FileSystem.AddAssign("RAM", "SYS:ram");

        Assert.Equal(0u, System("copy >NIL: C:assign ram:"));

        Assert.Equal("command", File.ReadAllText(Path.Combine(_harness.Root, "ram", "Assign")));
        Assert.Equal("", _harness.OutputText);
    }

    [Fact]
    public void Copy_WithAPatternAndAll_CopiesTheMatches()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Root, "gfx", "sub"));
        File.WriteAllText(Path.Combine(_harness.Root, "gfx", "a.iff"), "a");
        File.WriteAllText(Path.Combine(_harness.Root, "gfx", "b.iff"), "b");
        File.WriteAllText(Path.Combine(_harness.Root, "gfx", "sub", "c.iff"), "c");
        Directory.CreateDirectory(Path.Combine(_harness.Root, "work"));
        _harness.Core.FileSystem.AddAssign("GFX", "SYS:gfx");
        _harness.Core.FileSystem.AddAssign("WORK", "SYS:work");

        Assert.Equal(0u, System("COPY >NIL: GFX:#? WORK: CLONE ALL"));

        Assert.True(File.Exists(Path.Combine(_harness.Root, "work", "a.iff")));
        Assert.True(File.Exists(Path.Combine(_harness.Root, "work", "b.iff")));
        Assert.True(File.Exists(Path.Combine(_harness.Root, "work", "sub", "c.iff")));
    }

    [Fact]
    public void Copy_MissingSource_Fails()
    {
        Assert.Equal(20u, System("copy >NIL: nothing.dat SYS:"));
    }

    [Fact]
    public void Delete_WithATrailingSlash_DeletesTheContents()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Root, "logos"));
        File.WriteAllText(Path.Combine(_harness.Root, "logos", "one"), "1");
        File.WriteAllText(Path.Combine(_harness.Root, "logos", "two"), "2");

        Assert.Equal(0u, System("DELETE > NIL: SYS:logos/"));

        Assert.True(Directory.Exists(Path.Combine(_harness.Root, "logos")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_harness.Root, "logos")));
    }

    [Fact]
    public void List_QuickAndNoHead_WritesTheNamesToAFile()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Root, "logos"));
        File.WriteAllText(Path.Combine(_harness.Root, "logos", "b"), "");
        File.WriteAllText(Path.Combine(_harness.Root, "logos", "a"), "");

        Assert.Equal(0u, System("list >SYS:logodir.txt SYS:logos nohead quick"));

        Assert.Equal("a\nb\n", File.ReadAllText(Path.Combine(_harness.Root, "logodir.txt")));
    }

    [Fact]
    public void Assign_MakesANameForADirectory()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Root, "pc"));
        File.WriteAllText(Path.Combine(_harness.Root, "pc", "file"), "x");

        Assert.Equal(0u, System("Assign GFX: SYS:pc"));
        Assert.Equal(0u, System("copy GFX:file SYS:copy"));

        Assert.True(File.Exists(Path.Combine(_harness.Root, "copy")));
    }

    [Fact]
    public void Echo_WritesToTheConsole()
    {
        Assert.Equal(0u, System("echo \"hello there\""));

        Assert.Equal("hello there\n", _harness.OutputText);
    }

    [Fact]
    public void Mount_Fails_BecauseTheRuntimeHasNoDevices()
    {
        Assert.Equal(20u, System("Mount PC1:"));
    }

    [Fact]
    public void Execute_ReturnsTrueForAKnownCommand_AndFalseForAnUnknownCommand()
    {
        Assert.Equal(unchecked((uint)-1), _harness.Call(_harness.DosBase, Execute, ("D1", _harness.String("makedir SYS:new")), ("D2", 0), ("D3", 0)));
        Assert.True(Directory.Exists(Path.Combine(_harness.Root, "new")));
        Assert.Equal(0u, _harness.Call(_harness.DosBase, Execute, ("D1", _harness.String("frobnicate")), ("D2", 0), ("D3", 0)));
    }

    private uint System(string command) =>
        _harness.Call(_harness.DosBase, SystemTagList, ("D1", _harness.String(command)), ("D2", 0));

    public void Dispose() => _harness.Dispose();
}
