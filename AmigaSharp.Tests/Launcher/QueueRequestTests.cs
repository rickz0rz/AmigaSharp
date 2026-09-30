using System.Text.Json;
using AmigaSharp.Launcher;

namespace AmigaSharp.Tests.Launcher;

public sealed class QueueRequestTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("AmigaSharp-queue-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Black_NeedsSeconds_AndIsNotAFile()
    {
        var video = QueueRequest.ReadVideo(Json("""{"source": "black", "seconds": 180}"""));
        Assert.Equal(new QueueRequest.Video(GenlockPlaylist.Black, 180, false, false), video);

        Assert.Throws<FormatException>(() => QueueRequest.ReadVideo(Json("""{"source": "black"}""")));
    }

    [Fact]
    public void File_WithObject_HasTheLoop_AndItsFilesAreRelativeToIt()
    {
        File.WriteAllText(Path.Combine(_directory, "promo.mp4"), "");
        var path = Write("""
            {"loop": "all", "queue": [
                {"source": "promo.mp4"},
                {"source": "black", "seconds": 180},
                {"source": "http://tuner.local:5004/auto/v2", "seconds": 300}
            ]}
            """);

        var file = QueueRequest.ReadFile(path);

        Assert.True(file.LoopAll);
        Assert.Equal(
            [Path.Combine(_directory, "promo.mp4"), GenlockPlaylist.Black, "http://tuner.local:5004/auto/v2"],
            file.Videos.Select(video => video.Source));
        Assert.Equal([null, 180, 300], file.Videos.Select(video => video.Seconds));
    }

    [Fact]
    public void File_WithArray_KeepsTheLoopSetting()
    {
        var file = QueueRequest.ReadFile(Write("""[{"source": "black", "seconds": 5}]"""));

        Assert.Null(file.LoopAll);
        Assert.Single(file.Videos);
    }

    [Theory]
    [InlineData("""{"queue": [{"source": "missing.mp4"}]}""")]
    [InlineData("""{"loop": "sometimes", "queue": []}""")]
    [InlineData("""{"videos": []}""")]
    [InlineData("""not json""")]
    public void BadFile_GivesAnErrorWithItsPath(string text)
    {
        var path = Write(text);

        var error = Assert.Throws<FormatException>(() => QueueRequest.ReadFile(path));
        Assert.StartsWith(path, error.Message);
    }

    private string Write(string text)
    {
        var path = Path.Combine(_directory, "queue.json");
        File.WriteAllText(path, text);
        return path;
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
}
