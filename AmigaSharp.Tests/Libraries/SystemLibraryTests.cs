using AmigaSharp.Runtime.Graphics;
using AmigaSharp.Runtime.Libraries.Native;

namespace AmigaSharp.Tests.Libraries;

/// <summary>Tests of utility.library, battclock.resource and intuition.library.</summary>
public sealed class SystemLibraryTests : IDisposable
{
    private const short Amiga2Date = -120;
    private const short Date2Amiga = -126;
    private const short CheckDate = -132;
    private const short UDivMod32 = -156;
    private const short ReadBattClock = -12;
    private const short WriteBattClock = -18;
    private const short SizeWindow = -288;
    private const short OpenResource = -498;

    private readonly LibraryHarness _harness = new();

    [Fact]
    public void Amiga2Date_And_Date2Amiga_AreInverses()
    {
        var utility = _harness.Core.OpenLibrary("utility.library", 37)!.Base;
        var clockData = _harness.Core.AllocateSystem(14);
        // 1 March 1994, 12:34:56, a Tuesday.
        var seconds = (uint)(new DateTime(1994, 3, 1, 12, 34, 56) - new DateTime(1978, 1, 1)).TotalSeconds;

        _harness.Call(utility, Amiga2Date, ("D0", seconds), ("A0", clockData));

        var memory = _harness.Memory;
        Assert.Equal((56, 34, 12, 1, 3, 1994, 2),
            (memory.Read16(clockData), memory.Read16(clockData + 2), memory.Read16(clockData + 4), memory.Read16(clockData + 6),
                memory.Read16(clockData + 8), memory.Read16(clockData + 10), memory.Read16(clockData + 12)));
        Assert.Equal(seconds, _harness.Call(utility, Date2Amiga, ("A0", clockData)));

        memory.Write16(clockData + 8, 2); // 30 February
        memory.Write16(clockData + 6, 30);
        Assert.Equal(0u, _harness.Call(utility, CheckDate, ("A0", clockData)));
    }

    [Fact]
    public void UDivMod32_ReturnsTheQuotientAndTheRemainder()
    {
        var utility = _harness.Core.OpenLibrary("utility.library", 37)!.Base;

        _harness.Call(utility, UDivMod32, ("D0", 100), ("D1", 7));

        Assert.Equal((14u, 2u), (_harness.Core.Cpu.D[0], _harness.Core.Cpu.D[1]));
    }

    [Fact]
    public void BattClock_GivesTheTime_AndAcceptsANewTime()
    {
        var name = _harness.String("battclock.resource");
        var battClock = _harness.Call(_harness.ExecBase, OpenResource, ("A1", name));
        Assert.NotEqual(0u, battClock);

        var now = (DateTime.Now - new DateTime(1978, 1, 1)).TotalSeconds;
        Assert.InRange(_harness.Call(battClock, ReadBattClock), now - 5, now + 5);

        _harness.Call(battClock, WriteBattClock, ("D0", 1000));
        Assert.InRange(_harness.Call(battClock, ReadBattClock), 1000u, 1005u);
    }

    [Fact]
    public void Intuition_HasTheWorkbenchScreen_WithContiguousPlanes()
    {
        var intuition = _harness.Core.OpenLibrary("intuition.library", 0)!.Base;
        var memory = _harness.Memory;

        var screen = memory.Read32(intuition + IntuitionBaseOffsets.ActiveScreen);
        var bitMap = screen + ScreenOffsets.BitMap;

        Assert.Equal(IntuitionLibrary.ScreenDepth, memory.Read8(bitMap + BitMapOffsets.Depth));
        Assert.Equal(IntuitionLibrary.ScreenHeight, memory.Read16(screen + ScreenOffsets.Height));
        Assert.Equal(memory.Read32(bitMap + BitMapOffsets.Planes) + 80u * 200,
            memory.Read32(bitMap + BitMapOffsets.Planes + 4));
        Assert.Equal(bitMap, memory.Read32(screen + ScreenOffsets.RastPort + RastPortOffsets.BitMap));
    }

    [Fact]
    public void SizeWindow_ChangesTheSize()
    {
        var intuition = _harness.Core.OpenLibrary("intuition.library", 0)!.Base;
        var window = _harness.Memory.Read32(intuition + IntuitionBaseOffsets.ActiveWindow);
        var height = _harness.Memory.Read16(window + WindowOffsets.Height);

        _harness.Call(intuition, SizeWindow, ("A0", window), ("D0", 0), ("D1", unchecked((uint)-100)));

        Assert.Equal(height - 100, _harness.Memory.Read16(window + WindowOffsets.Height));
    }

    public void Dispose() => _harness.Dispose();
}
