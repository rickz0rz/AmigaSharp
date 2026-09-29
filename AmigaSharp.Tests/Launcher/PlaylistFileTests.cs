using AmigaSharp.Launcher;

namespace AmigaSharp.Tests.Launcher;

public sealed class PlaylistFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("AmigaSharp-audio-").FullName;

    [Fact]
    public void Read_SkipsComments_AndMissingFiles_AndResolvesRelativePaths()
    {
        var music = Path.Combine(_directory, "music");
        Directory.CreateDirectory(music);
        File.WriteAllText(Path.Combine(music, "one.mp3"), "");
        var absolute = Path.Combine(_directory, "two.wav");
        File.WriteAllText(absolute, "");
        var playlist = Path.Combine(_directory, "list.m3u");
        File.WriteAllLines(playlist, ["#EXTM3U", "#EXTINF:120,One", "music/one.mp3", "", "missing.mp3", absolute]);

        Assert.Equal([Path.Combine(music, "one.mp3"), absolute], PlaylistFile.Read(playlist));
    }

    [Fact]
    public void Read_OfADirectory_HasItsAudioFiles_InTheOrderOfTheirNames()
    {
        foreach (var name in new[] { "b.MP3", "a.flac", "cover.jpg", "c.wav" })
            File.WriteAllText(Path.Combine(_directory, name), "");

        var names = PlaylistFile.Read(_directory).Select(Path.GetFileName);

        Assert.Equal(["a.flac", "b.MP3", "c.wav"], names);
    }

    [Fact]
    public void Read_OfAPathThatDoesNotExist_IsEmpty()
    {
        Assert.Empty(PlaylistFile.Read(Path.Combine(_directory, "nothing.m3u")));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
