using AmigaSharp.Host;

namespace AmigaSharp.Tests.Launcher;

public sealed class TempFoldersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("AmigaSharp-temp-test-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RemoveOld_RemovesTheFoldersOfStoppedLaunchers_AndKeepsTheOthers()
    {
        var stopped = Folder("AmigaSharp-RAM-stopped", owner: "100\n5000\n");
        var running = Folder("AmigaSharp-stream-running", owner: "200\n6000\n");
        var oldOrphan = Folder("AmigaSharp-Disk-old", owner: null);
        Directory.SetLastWriteTimeUtc(oldOrphan, DateTime.UtcNow.AddHours(-2));
        var newOrphan = Folder("AmigaSharp-RAM-new", owner: null);
        var other = Folder("Other-RAM-x", owner: null);
        Directory.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-2));

        var removed = TempFolders.RemoveOld(TextWriter.Null, _root, (id, start) => id == 200 && start == 6000);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(stopped));
        Assert.False(File.Exists(stopped + ".owner"));
        Assert.False(Directory.Exists(oldOrphan));
        Assert.True(Directory.Exists(running));
        Assert.True(Directory.Exists(newOrphan));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void Create_WritesTheOwner_AndDeleteRemovesTheTwo()
    {
        var folder = TempFolders.Create("AmigaSharp-RAM-");
        try
        {
            var owner = File.ReadAllLines(folder + ".owner");
            Assert.Equal(Environment.ProcessId.ToString(), owner[0]);
            // This process runs, so its folder is not old.
            Assert.Equal(0, TempFolders.RemoveOld(TextWriter.Null, Path.GetDirectoryName(folder),
                (id, _) => id == Environment.ProcessId));
        }
        finally
        {
            TempFolders.Delete(folder);
        }

        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(folder + ".owner"));
    }

    private string Folder(string name, string? owner)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "file"), "data");
        if (owner != null)
            File.WriteAllText(folder + ".owner", owner);
        return folder;
    }
}
