namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// battclock.resource: the battery-backed clock. It gives the local time of the host. WriteBattClock changes the
/// time that this resource gives, not the time of the host.
/// </summary>
public class BattClockResource : AbstractLibrary
{
    private static readonly DateTime Epoch = new(1978, 1, 1);

    private TimeSpan _offset;

    public override string Name => "battclock.resource";
    public override ushort Version => 40;
    public override short LowestOffset => -18;

    /// <summary>The time of the clock: seconds from 1 January 1978.</summary>
    public uint Seconds => (uint)(DateTime.Now + _offset - Epoch).TotalSeconds;

    // ResetBattClock()
    [LibraryFunctionOffset(-6)]
    public void ResetBattClock() => _offset = -(DateTime.Now - Epoch);

    // amigaTime = ReadBattClock()
    // D0
    [LibraryFunctionOffset(-12)]
    public uint ReadBattClock() => Seconds;

    // WriteBattClock(amigaTime)
    //                D0
    [LibraryFunctionOffset(-18)]
    public void WriteBattClock([D0] uint seconds) => _offset = Epoch.AddSeconds(seconds) - DateTime.Now;
}
