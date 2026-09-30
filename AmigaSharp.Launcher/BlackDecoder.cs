namespace AmigaSharp.Launcher;

/// <summary>
/// The decoder of <see cref="GenlockPlaylist.Black"/>: black pictures and silence, with no end. The time limit of its
/// item ends it.
/// </summary>
public sealed class BlackDecoder : IGenlockDecoder
{
    public BlackDecoder() => Audio.Complete();

    // A complete buffer with no data gives silence at once.
    public PcmBuffer Audio { get; } = new();

    public bool HasSound => false;

    // A constant level, so that the stream keeps its rate.
    public int BufferedFrames => GenlockPlaylist.TargetFrames;

    public bool IsCompleted => false;

    public bool TryTake(out uint[] pixels, TimeSpan timeout)
    {
        // The stream gives each picture back to the pool of the decoders, so each picture is a new one.
        pixels = GenlockDecoder.Rent();
        Array.Fill(pixels, 0xFF000000);
        return true;
    }

    public void Dispose()
    {
    }
}
