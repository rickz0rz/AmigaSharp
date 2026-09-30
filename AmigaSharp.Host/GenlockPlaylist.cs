using System.Text.Json;

namespace AmigaSharp.Host;

/// <summary>
/// The queue of videos that the genlock shows behind the display. Each item is a file or a URL, with an optional time
/// limit. The stream asks for a picture on each tick with <see cref="TakeFrame"/>. With no item, it gets black.
/// </summary>
/// <remarks>
/// <para>
/// A file plays to its end, or in a loop. A URL (a live source) plays until its time ends or until a skip. If a live
/// source stops before its time ends, the playlist starts it again after 2 seconds. An item without a time limit plays
/// until it ends or until a skip. The source <see cref="Black"/> is black and silence for its time.
/// </para>
/// <para>
/// The time of an item counts the ticks of the stream while the item is current, also the ticks that show black while
/// its decoder starts. So an item of 300 seconds is 300 seconds of the stream. A few seconds before an item ends, the
/// decoder of the next item starts, so the next item shows without a gap. For a live item without a time limit, the
/// end is not known, so the next item starts after a skip, with black while its decoder starts.
/// </para>
/// </remarks>
public sealed class GenlockPlaylist : IDisposable
{
    /// <summary>The stream starts the pictures of a decoder when it has this number, and keeps it near there.</summary>
    public const int TargetFrames = 6;

    /// <summary>
    /// The source of an item that is black and silence, for example a pause between two videos. It needs a time limit.
    /// </summary>
    public const string Black = "black";

    private const double FramesPerSecond = 30000 / 1001.0;
    private static readonly TimeSpan PreloadTime = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly LinkedList<Item> _queue = new();
    private readonly TextWriter _log;
    private readonly Func<Item, IGenlockDecoder> _startDecoder;
    private readonly TimeSpan _restartDelay;
    private readonly string _name;
    private int _nextId = 1;
    private Item? _current;
    private IGenlockDecoder? _decoder;
    private IGenlockDecoder? _preloaded;
    private long _currentFrames;
    private long _currentPictures;
    private bool _loopAll;
    private bool _playing;
    private DateTime? _restartAt;

    /// <param name="withAudio">True to decode the sound of the videos too.</param>
    /// <param name="startDecoder">Starts the decoder of an item. The default is a <see cref="GenlockDecoder"/>.</param>
    /// <param name="restartDelay">The time before a live source that stopped starts again. The default is 2 seconds.</param>
    /// <param name="name">The name of the playlist in the log, for example "Genlock" or "Music".</param>
    public GenlockPlaylist(bool withAudio, TextWriter log, Func<Item, IGenlockDecoder>? startDecoder = null,
        TimeSpan? restartDelay = null, string name = "Genlock")
    {
        _name = name;
        _log = log;
        _restartDelay = restartDelay ?? TimeSpan.FromSeconds(2);
        _startDecoder = startDecoder ?? (item => new GenlockDecoder(item.Source, item.Loop, withAudio, log));
    }

    /// <summary>An item of the playlist.</summary>
    /// <param name="Seconds">The time limit, or null to play to the end.</param>
    /// <param name="Duration">The duration of a file, or null for a live source.</param>
    public sealed record Item(int Id, string Source, double? Seconds, bool Loop, double? Duration)
    {
        /// <summary>The number of ticks that the item plays, or null if it plays until it ends or until a skip.</summary>
        public long? Frames => Seconds is { } seconds ? (long)Math.Round(seconds * FramesPerSecond)
            : Duration is { } duration && !Loop ? (long)Math.Round(duration * FramesPerSecond) : null;
    }

    /// <summary>A picture of the current item, the sound of its item, and if the item has sound.</summary>
    public readonly record struct Frame(uint[] Pixels, PcmBuffer Audio, bool HasSound);

    /// <summary>The number of pictures that the current decoder has ready. The stream keeps it near the target.</summary>
    public int BufferedFrames
    {
        get
        {
            lock (_lock)
                return _decoder?.BufferedFrames ?? 0;
        }
    }

    /// <summary>True if the current item shows its pictures now.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (_lock)
                return _playing;
        }
    }

    /// <summary>Adds an item at the end of the queue, or after the current item.</summary>
    /// <param name="seconds">The time limit, or null to play to the end.</param>
    /// <param name="loop">True to play a file in a loop.</param>
    /// <param name="next">True to put the item first in the queue.</param>
    public Item Add(string source, double? seconds, bool loop, bool next)
    {
        var item = new Item(0, source, seconds, loop, source == Black ? null : GenlockDecoder.Duration(source));
        lock (_lock)
        {
            item = item with { Id = _nextId++ };
            if (next)
            {
                _queue.AddFirst(item);
                // The item that was first can have a decoder already.
                _preloaded?.Dispose();
                _preloaded = null;
            }
            else
            {
                _queue.AddLast(item);
            }
        }

        _log.WriteLine($"{_name}: queued {Describe(item)}.");
        return item;
    }

    /// <summary>Removes an item from the queue. Returns false if the queue does not have it.</summary>
    public bool Remove(int id)
    {
        lock (_lock)
        {
            var node = _queue.First;
            while (node != null && node.Value.Id != id)
                node = node.Next;
            if (node == null)
                return false;
            if (node == _queue.First)
            {
                _preloaded?.Dispose();
                _preloaded = null;
            }

            _queue.Remove(node);
            return true;
        }
    }

    /// <summary>
    /// True to play the queue in a loop: an item that ends, or that is skipped, goes to the end of the queue again. An
    /// item that gave no picture (a file that does not play, for example) does not.
    /// </summary>
    public bool LoopAll
    {
        get
        {
            lock (_lock)
                return _loopAll;
        }
        set
        {
            lock (_lock)
                _loopAll = value;
        }
    }

    /// <summary>Ends the current item. The next item starts, or black shows.</summary>
    public void Skip()
    {
        lock (_lock)
            EndCurrent("skipped");
    }

    /// <summary>Removes all items from the queue and ends the current item, also in a loop.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _queue.Clear();
            _preloaded?.Dispose();
            _preloaded = null;
            EndCurrent("stopped", requeue: false);
        }
    }

    /// <summary>Removes all items from the queue. The current item continues.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _preloaded?.Dispose();
            _preloaded = null;
        }
    }

    /// <summary>
    /// Gives the picture for the next tick of the stream, or null for black. The time of the current item moves one
    /// tick. The call waits up to <paramref name="timeout"/> for a picture that is late.
    /// </summary>
    public Frame? TakeFrame(TimeSpan timeout)
    {
        IGenlockDecoder? decoder;
        lock (_lock)
        {
            Advance();
            if (_current == null)
                return null;
            _currentFrames++;
            decoder = _decoder;
            if (decoder == null)
                return null;
            if (!_playing)
            {
                if (decoder.BufferedFrames < TargetFrames && !decoder.IsCompleted)
                    return null;
                _playing = true;
            }
        }

        if (decoder.TryTake(out var pixels, timeout))
        {
            lock (_lock)
            {
                if (_decoder == decoder)
                    _currentPictures++;
            }

            return new Frame(pixels, decoder.Audio, decoder.HasSound);
        }

        // The decoder has no picture: black until it has pictures again.
        lock (_lock)
        {
            if (_decoder == decoder)
                _playing = false;
        }

        return null;
    }

    /// <summary>The current item and the queue, as JSON.</summary>
    public string ToJson()
    {
        lock (_lock)
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WritePropertyName("current");
                if (_current == null)
                {
                    json.WriteNullValue();
                }
                else
                {
                    WriteItem(json, _current);
                    json.WriteNumber("played", Math.Round(_currentFrames / FramesPerSecond, 1));
                    json.WriteString("state", _playing ? "playing" : _decoder == null ? "waiting" : "starting");
                    json.WriteEndObject();
                }

                json.WriteString("loop", _loopAll ? "all" : "off");
                json.WriteStartArray("queue");
                foreach (var item in _queue)
                {
                    WriteItem(json, item);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _decoder?.Dispose();
            _preloaded?.Dispose();
            _decoder = _preloaded = null;
        }
    }

    /// <summary>Starts an item when none plays, ends an item whose time is over, and starts the decoders.</summary>
    private void Advance()
    {
        if (_current != null)
        {
            if (_current.Frames is { } frames && _currentFrames >= frames)
            {
                EndCurrent("ended");
            }
            else if (_decoder is { IsCompleted: true })
            {
                // A file that ended ends its item. A live source that stopped starts again, while the item has time.
                if (File.Exists(_current.Source) && !_current.Loop)
                {
                    EndCurrent("ended");
                }
                else
                {
                    _log.WriteLine($"{_name}: {_current.Source} stopped. It starts again in {_restartDelay.TotalSeconds:F0} seconds.");
                    _decoder.Dispose();
                    _decoder = null;
                    _playing = false;
                    _restartAt = DateTime.UtcNow + _restartDelay;
                }
            }
        }

        if (_current == null && _queue.First is { } first)
        {
            _queue.RemoveFirst();
            _current = first.Value;
            _currentFrames = 0;
            _currentPictures = 0;
            _playing = false;
            _decoder = _preloaded ?? StartDecoder(_current);
            _preloaded = null;
            _log.WriteLine($"{_name}: playing {Describe(_current)}.");
        }

        if (_current != null && _decoder == null && DateTime.UtcNow >= _restartAt)
        {
            _decoder = StartDecoder(_current);
            _restartAt = null;
        }

        // Start the decoder of the next item a few seconds before the current item ends.
        if (_preloaded == null && _queue.First is { } next && _current?.Frames is { } total
            && total - _currentFrames <= PreloadTime.TotalSeconds * FramesPerSecond)
            _preloaded = StartDecoder(next.Value);
    }

    private IGenlockDecoder StartDecoder(Item item) => item.Source == Black ? new BlackDecoder() : _startDecoder(item);

    private void EndCurrent(string reason, bool requeue = true)
    {
        if (_current == null)
            return;
        _log.WriteLine($"{_name}: {reason} {Describe(_current)}.");
        if (requeue && _loopAll && _currentPictures > 0)
            _queue.AddLast(_current);
        _decoder?.Dispose();
        _decoder = null;
        _current = null;
        _playing = false;
        _restartAt = null;
    }

    private static void WriteItem(Utf8JsonWriter json, Item item)
    {
        json.WriteStartObject();
        json.WriteNumber("id", item.Id);
        json.WriteString("source", item.Source);
        if (item.Seconds is { } seconds)
            json.WriteNumber("seconds", seconds);
        else
            json.WriteNull("seconds");
        json.WriteBoolean("loop", item.Loop);
        if (item.Duration is { } duration)
            json.WriteNumber("duration", Math.Round(duration, 1));
    }

    private static string Describe(Item item) =>
        $"#{item.Id} {item.Source}" + (item.Seconds is { } seconds ? $" for {seconds:0.#} s" : "") + (item.Loop ? " in a loop" : "");
}
