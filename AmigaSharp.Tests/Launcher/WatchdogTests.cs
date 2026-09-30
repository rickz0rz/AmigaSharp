using AmigaSharp.Host;

namespace AmigaSharp.Tests.Launcher;

public class WatchdogTests
{
    private uint _picture;
    private int _stops;

    [Fact]
    public void MovingPicture_DoesNotStopTheProgram()
    {
        var watchdog = Create(limit: 10);

        for (var second = 0; second < 60; second++)
        {
            _picture++;
            Assert.False(watchdog.Check(TimeSpan.FromSeconds(second)));
        }

        Assert.Equal(0, _stops);
    }

    [Fact]
    public void StillPicture_StopsTheProgram_OneTime_AfterTheLimit()
    {
        var watchdog = Create(limit: 10);

        Assert.False(watchdog.Check(TimeSpan.FromSeconds(0)));
        Assert.False(watchdog.Check(TimeSpan.FromSeconds(9)));
        Assert.True(watchdog.Check(TimeSpan.FromSeconds(10)));
        Assert.True(watchdog.Check(TimeSpan.FromSeconds(11)));

        Assert.Equal(1, _stops);
    }

    [Fact]
    public void Change_StartsTheTimeAgain()
    {
        var watchdog = Create(limit: 10);

        watchdog.Check(TimeSpan.FromSeconds(0));
        _picture++;
        watchdog.Check(TimeSpan.FromSeconds(8));

        Assert.False(watchdog.Check(TimeSpan.FromSeconds(17)));
        Assert.True(watchdog.Check(TimeSpan.FromSeconds(18)));
    }

    private Watchdog Create(double limit) =>
        new(pixels => Array.Fill(pixels, _picture), TimeSpan.FromSeconds(limit), TextWriter.Null, () => _stops++,
            start: false);
}
