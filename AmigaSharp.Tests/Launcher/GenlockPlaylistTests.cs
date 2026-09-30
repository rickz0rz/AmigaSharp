using System.Text.Json;
using AmigaSharp.Host;

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

    [Fact]
    public void LoopAll_PutsAnItemThatEnded_OrWasSkipped_AtTheEndOfTheQueue()
    {
        _playlist.LoopAll = true;
        _playlist.Add(Live, seconds: 1, loop: false, next: false);
        _playlist.Add(OtherLive, seconds: null, loop: false, next: false);
        _playlist.TakeFrame(TimeSpan.Zero);
        _started[0].Decoder.Add(100);
        _started[1].Decoder.Add(100);
        for (var tick = 1; tick < 31; tick++)
            _playlist.TakeFrame(TimeSpan.Zero);

        Assert.Equal(OtherLive, Json().GetProperty("current").GetProperty("source").GetString());
        Assert.Equal([Live], Queue());
        Assert.Equal("all", Json().GetProperty("loop").GetString());

        _playlist.Skip();
        Assert.Equal([Live, OtherLive], Queue());
    }

    [Fact]
    public void Black_IsBlackAndSilence_ForItsSeconds_AndLoops()
    {
        _playlist.LoopAll = true;
        _playlist.Add(GenlockPlaylist.Black, seconds: 1, loop: false, next: false);
        _playlist.Add(Live, seconds: null, loop: false, next: false);

        // One second is 30 ticks. The black item needs no decoder of the factory.
        for (var tick = 0; tick < 30; tick++)
        {
            var frame = _playlist.TakeFrame(TimeSpan.Zero);
            Assert.NotNull(frame);
            Assert.False(frame.Value.HasSound);
            Assert.All(frame.Value.Pixels, pixel => Assert.Equal(0xFF000000u, pixel));
            GenlockDecoder.Return(frame.Value.Pixels);
        }

        Assert.Equal([Live], _started.Select(started => started.Item.Source));
        _playlist.TakeFrame(TimeSpan.Zero);
        Assert.Equal(Live, Json().GetProperty("current").GetProperty("source").GetString());
        Assert.Equal([GenlockPlaylist.Black], Queue());
    }

    [Fact]
    public void LoopAll_DropsAnItemThatGaveNoPicture()
    {
        _playlist.LoopAll = true;
        _playlist.Add(_file, seconds: null, loop: false, next: false);
        _playlist.TakeFrame(TimeSpan.Zero);
        _started[0].Decoder.Complete();

        _playlist.TakeFrame(TimeSpan.Zero);

        Assert.Equal(JsonValueKind.Null, Json().GetProperty("current").ValueKind);
        Assert.Empty(Queue());
    }

    [Fact]
    public void Stop_EndsTheCurrentItem_AlsoInALoop()
    {
        _playlist.LoopAll = true;
        _playlist.Add(Live, seconds: null, loop: false, next: false);
        _playlist.Add(OtherLive, seconds: null, loop: false, next: false);
        _playlist.TakeFrame(TimeSpan.Zero);
        _started[0].Decoder.Add(GenlockPlaylist.TargetFrames);
        _playlist.TakeFrame(TimeSpan.Zero);

        _playlist.Stop();

        Assert.Equal(JsonValueKind.Null, Json().GetProperty("current").ValueKind);
        Assert.Empty(Queue());
    }

    private List<string?> Queue() =>
        Json().GetProperty("queue").EnumerateArray().Select(item => item.GetProperty("source").GetString()).ToList();

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
