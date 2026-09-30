using System.Text.Json;
using AmigaSharp.Host;
using AmigaSharp.PrevueLauncher;

namespace AmigaSharp.Tests.Launcher;

public sealed class ScheduleTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("AmigaSharp-schedule-").FullName;
    private readonly IScheduleExtension[] _prevue =
        [new PrevueSchedule(new ControlLineRequests(new ControlLineFeed()), state: null, TextWriter.Null)];

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Schedule_HasVideosAndPauses_WithMusic_AndFilesRelativeToIt()
    {
        File.WriteAllText(Path.Combine(_directory, "promo.mp4"), "");

        var schedule = Read("""
            {"loop": true, "segments": [
                {"video": "promo.mp4", "music": {"volume": 0.3, "fade": 2}},
                {"video": "http://tuner.local:5004/auto/v2", "seconds": 300},
                {"pause": 180}
            ]}
            """, []);

        Assert.True(schedule.Loop);
        Assert.Equal(
            [Path.Combine(_directory, "promo.mp4"), "http://tuner.local:5004/auto/v2", GenlockPlaylist.Black],
            schedule.Segments.Select(segment => segment.Video.Source));
        Assert.Equal([null, 300, 180], schedule.Segments.Select(segment => segment.Video.Seconds));
        Assert.Equal(new LayerSettings(0.3, false, 2), schedule.Segments[0].Music);
        Assert.Null(schedule.Segments[1].Music);
    }

    [Theory]
    [InlineData("""{"segments": []}""")]
    [InlineData("""{"segments": [{"pause": 0}]}""")]
    [InlineData("""{"segments": [{"video": "missing.mp4"}]}""")]
    [InlineData("""{"segments": [{"video": "http://x.local/v", "pause": 5}]}""")]
    [InlineData("""{"segments": [{"pause": 5, "top": "clear"}]}""")]
    public void BadSchedule_OrAKeyWithoutItsExtension_IsAnError(string text)
    {
        Assert.Throws<FormatException>(() => Read(text, []));
    }

    [Fact]
    public void PrevueExtension_ReadsTheTopHalf()
    {
        var schedule = Read("""
            {"segments": [
                {"pause": 60, "top": "clear"},
                {"pause": 60, "top": "logos"},
                {"pause": 60, "top": [
                    {"promo": "Seinfeld", "seconds": 20},
                    {"promo": {"left": {"title": "Bob's Burgers"}}, "seconds": 20},
                    {"logo": "Insider", "seconds": 10},
                    {"clear": true}
                ]}
            ]}
            """, _prevue);

        Assert.Equal(["top"], schedule.Segments[2].Keys.Keys);
    }

    [Theory]
    [InlineData("\"sometimes\"")]
    [InlineData("[]")]
    [InlineData("""[{"promo": "Seinfeld"}, {"clear": true}]""")]
    [InlineData("""[{"logo": 5, "seconds": 10}]""")]
    [InlineData("""[{"wait": 10}]""")]
    [InlineData("""[{"promo": {"title": ""}, "seconds": 10}]""")]
    public void PrevueExtension_RefusesABadTopHalf(string top)
    {
        Assert.Throws<FormatException>(() => Read($$"""{"segments": [{"pause": 60, "top": {{top}}}]}""", _prevue));
    }

    private Schedule Read(string text, IReadOnlyList<IScheduleExtension> extensions)
    {
        using var document = JsonDocument.Parse(text);
        return Schedule.Parse(document.RootElement, _directory, extensions);
    }
}
