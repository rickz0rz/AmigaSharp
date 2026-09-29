using System.Text.Json;
using AmigaSharp.Launcher;

namespace AmigaSharp.Tests.Launcher;

public sealed class GenlockPlaylistTests : IDisposable
{
    private const string Live = "http://tuner.local:5004/auto/v2";
    private const string OtherLive = "http://tuner.local:5004/auto/v1002";

    private readonly List<(GenlockPlaylist.Item Item, FakeDecoder Decoder)> _started = [];
    private readonly GenlockPlaylist _playlist;
    private readonly string _file = Path.GetTempFileName();

    public GenlockPlaylistTests()
    {
        _playlist = new GenlockPlaylist(withAudio: true, TextWriter.Null, item =>
        {
            var decoder = new FakeDecoder();
            _started.Add((item, decoder));
            return decoder;
        }, restartDelay: TimeSpan.Zero);
    }

    public void Dispose()
    {
        _playlist.Dispose();
        File.Delete(_file);
    }

    [Fact]
    public void EmptyPlaylist_GivesBlack()
    {
        Assert.Null(_playlist.TakeFrame(TimeSpan.Zero));
        Assert.Empty(_started);
    }

    [Fact]
    public void Item_ShowsBlack_UntilItsDecoderHasTheTargetFrames()
    {
        _playlist.Add(Live, seconds: null, loop: false, next: false);
        Assert.Null(_playlist.TakeFrame(TimeSpan.Zero));
        var decoder = Assert.Single(_started).Decoder;

        decoder.Add(GenlockPlaylist.TargetFrames - 1);
        Assert.Null(_playlist.TakeFrame(TimeSpan.Zero));

        decoder.Add(1);
        Assert.NotNull(_playlist.TakeFrame(TimeSpan.Zero));
        Assert.True(_playlist.IsPlaying);
    }

    [Fact]
    public void TimedItem_EndsAfterItsSeconds_AndTheNextItemIsReadyThen()
    {
        // One second is 30 ticks at 29.97 pictures each second.
        _playlist.Add(Live, seconds: 1, loop: false, next: false);
        _playlist.Add(OtherLive, seconds: null, loop: false, next: false);
        _playlist.TakeFrame(TimeSpan.Zero);
        // The item ends within 5 seconds, so the decoder of the next item starts at once.
        Assert.Equal([Live, OtherLive], _started.Select(started => started.Item.Source));
        _started[0].Decoder.Add(100);
        _started[1].Decoder.Add(100);

        for (var tick = 1; tick < 30; tick++)
            Assert.Same(_started[0].Decoder.Audio, _playlist.TakeFrame(TimeSpan.Zero)?.Audio);

        Assert.Same(_started[1].Decoder.Audio, _playlist.TakeFrame(TimeSpan.Zero)?.Audio);
        Assert.True(_started[0].Decoder.Disposed);
    }

    [Fact]
    public void File_EndsWhenItsDecoderEnds_AndThenBlackShows()
    {
        _playlist.Add(_file, seconds: null, loop: false, next: false);
        _playlist.TakeFrame(TimeSpan.Zero);
        var decoder = Assert.Single(_started).Decoder;
        decoder.Add(GenlockPlaylist.TargetFrames);
        decoder.Complete();

        for (var tick = 0; tick < GenlockPlaylist.TargetFrames; tick++)
            Assert.NotNull(_playlist.TakeFrame(TimeSpan.Zero));

        Assert.Null(_playlist.TakeFrame(TimeSpan.Zero));
        Assert.Null(_playlist.TakeFrame(TimeSpan.Zero));
        Assert.Equal(JsonValueKind.Null, Json().GetProperty("current").ValueKind);
    }

    [Fact]
    public void LiveSource_StartsAgain_WhenItStopsBeforeItsTimeEnds()
    {
        _playlist.Add(Live, seconds: 10, loop: false, next: false);
        _playlist.TakeFrame(TimeSpan.Zero);
        _started[0].Decoder.Complete();

        _playlist.TakeFrame(TimeSpan.Zero);
        _playlist.TakeFrame(TimeSpan.Zero);

        Assert.Equal(2, _started.Count(started => started.Item.Source == Live));
        Assert.Equal(Live, Json().GetProperty("current").GetProperty("source").GetString());
    }

    [Fact]
    public void Skip_EndsTheCurrentItem_AndNextPutsAnItemFirst()
    {
        _playlist.Add(Live, seconds: null, loop: false, next: false);
        _playlist.TakeFrame(TimeSpan.Zero);
        _playlist.Add(OtherLive, seconds: 60, loop: false, next: false);
        _playlist.Add(_file, seconds: null, loop: true, next: true);

        _playlist.Skip();
        _playlist.TakeFrame(TimeSpan.Zero);

        var json = Json();
        Assert.Equal(_file, json.GetProperty("current").GetProperty("source").GetString());
        Assert.Equal([OtherLive], json.GetProperty("queue").EnumerateArray().Select(item => item.GetProperty("source").GetString()));
        Assert.True(_started[0].Decoder.Disposed);
    }

    [Fact]
    public void ClearAndRemove_ChangeOnlyTheQueue()
    {
        _playlist.Add(Live, seconds: null, loop: false, next: false);
        _playlist.TakeFrame(TimeSpan.Zero);
        var second = _playlist.Add(OtherLive, seconds: 60, loop: false, next: false);
        _playlist.Add(_file, seconds: null, loop: false, next: false);

        Assert.True(_playlist.Remove(second.Id));
        Assert.False(_playlist.Remove(second.Id));
        Assert.Single(Json().GetProperty("queue").EnumerateArray());

        _playlist.Clear();
        Assert.Empty(Json().GetProperty("queue").EnumerateArray());
        Assert.Equal(Live, Json().GetProperty("current").GetProperty("source").GetString());
    }

    private JsonElement Json() => JsonDocument.Parse(_playlist.ToJson()).RootElement;

    private sealed class FakeDecoder : IGenlockDecoder
    {
        private int _frames;
        private bool _complete;

        public PcmBuffer Audio { get; } = new();
        public bool HasSound => true;
        public int BufferedFrames => _frames;
        public bool IsCompleted => _complete && _frames == 0;
        public bool Disposed { get; private set; }

        public void Add(int frames) => _frames += frames;

        public void Complete() => _complete = true;

        public bool TryTake(out uint[] pixels, TimeSpan timeout)
        {
            pixels = new uint[1];
            if (_frames == 0)
                return false;
            _frames--;
            return true;
        }

        public void Dispose() => Disposed = true;
    }
}
