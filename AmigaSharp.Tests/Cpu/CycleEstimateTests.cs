using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Cpu;

namespace AmigaSharp.Tests.Cpu;

/// <summary>The estimates of common instructions, against the 68000 timing tables.</summary>
public class CycleEstimateTests
{
    [Theory]
    [InlineData(new ushort[] { 0x7001 }, 4)] // MOVEQ #1,D0
    [InlineData(new ushort[] { 0x2001 }, 4)] // MOVE.L D1,D0
    [InlineData(new ushort[] { 0x2010 }, 12)] // MOVE.L (A0),D0
    [InlineData(new ushort[] { 0x3080 }, 8)] // MOVE.W D0,(A0)
    [InlineData(new ushort[] { 0xD081 }, 8)] // ADD.L D1,D0
    [InlineData(new ushort[] { 0x5250 }, 12)] // ADDQ.W #1,(A0)
    [InlineData(new ushort[] { 0x4E75 }, 16)] // RTS
    [InlineData(new ushort[] { 0xC0C1 }, 70)] // MULU D1,D0 (38 to 70 on a real 68000)
    public void Estimate_IsNearTheTimingTables(ushort[] words, int cycles)
    {
        var memory = new Memory();
        for (var i = 0; i < words.Length; i++)
            memory.Write16(0x1000 + (uint)i * 2, words[i]);

        Assert.Equal(cycles, CycleEstimate.Of(Decoder.Decode(0x1000, memory.Read16)));
    }
}
