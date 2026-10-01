using AmigaSharp.Tests.Libraries;

namespace AmigaSharp.Tests.Graphics;

/// <summary>Tests of the View functions of graphics.library: the copper list that MrgCop makes from a View.</summary>
public sealed class GraphicsViewTests : IDisposable
{
    private const short LoadRgb4 = -192;
    private const short InitVPort = -204;
    private const short MrgCop = -210;
    private const short LoadView = -222;
    private const short SetRgb4 = -288;
    private const short InitView = -360;
    private const short GetColorMap = -570;
    private const short SetRgb4Cm = -630;

    private readonly LibraryHarness _harness = new();
    private readonly uint _graphics;

    public GraphicsViewTests()
    {
        _graphics = _harness.Core.OpenLibrary("graphics.library", 0)!.Base;
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void MrgCop_MakesTheCopperListOfAViewPort_AndSetRgb4ChangesItsColor()
    {
        var memory = _harness.Core.Memory;
        var view = _harness.Core.AllocateSystem(18);
        var viewPort = _harness.Core.AllocateSystem(40);
        var rasInfo = _harness.Core.AllocateSystem(12);
        var bitMap = _harness.Core.AllocateSystem(40);
        _harness.Call(_graphics, InitView, ("A1", view));
        _harness.Call(_graphics, InitVPort, ("A0", viewPort));
        var colorMap = _harness.Call(_graphics, GetColorMap, ("D0", 4));
        // A low-resolution ViewPort of 320 by 200 pixels with 2 planes of 40 bytes on each row.
        memory.Write32(view, viewPort);
        memory.Write32(viewPort + 4, colorMap);
        memory.Write16(viewPort + 24, 320);
        memory.Write16(viewPort + 26, 200);
        memory.Write32(viewPort + 36, rasInfo);
        memory.Write32(rasInfo + 4, bitMap);
        memory.Write16(bitMap, 40);
        memory.Write8(bitMap + 5, 2);
        memory.Write32(bitMap + 8, 0x1_0000);
        memory.Write32(bitMap + 12, 0x1_2000);
        _harness.Call(_graphics, SetRgb4Cm, ("A0", colorMap), ("D0", 1), ("D1", 15), ("D2", 0), ("D3", 0));

        _harness.Call(_graphics, MrgCop, ("A1", view));

        var list = memory.Read32(memory.Read32(view + 4) + 4);
        var moves = Moves(list);
        Assert.Equal(0x2C81, moves[0x08E]); // DIWSTRT: the standard position of InitView.
        Assert.Equal(0xF4C1, moves[0x090]); // DIWSTOP: 200 lines and 320 pixels later.
        Assert.Equal(0x38, moves[0x092]);
        Assert.Equal(0xD0, moves[0x094]);
        Assert.Equal(0x2200, moves[0x100]); // 2 planes, color.
        Assert.Equal(0, moves[0x108]);
        Assert.Equal(0x0001, moves[0x0E4]);
        Assert.Equal(0x2000, moves[0x0E6]);
        Assert.Equal(0xF00, moves[0x182]);

        // SetRGB4 changes the color in the ColorMap and in the copper list. LoadRGB4 does it for a table.
        _harness.Call(_graphics, SetRgb4, ("A0", viewPort), ("D0", 1), ("D1", 0), ("D2", 15), ("D3", 0));
        Assert.Equal(0x0F0, Moves(list)[0x182]);
        var colors = _harness.Core.AllocateSystem([0x01, 0x23, 0x04, 0x56]);
        _harness.Call(_graphics, LoadRgb4, ("A0", viewPort), ("A1", colors), ("D0", 2));
        Assert.Equal(0x456, Moves(list)[0x182]);

        // LoadView shows the list, and turns on the copper and the bitplanes.
        _harness.Call(_graphics, LoadView, ("A1", view));
        var custom = _harness.Core.Chipset.Custom;
        Assert.Equal(list, (uint)(custom[0x080] << 16 | custom[0x082]));
        Assert.Equal(0x0380, _harness.Core.Chipset.Custom.Dmacon & 0x0380);
    }

    /// <summary>The value of each register that the copper list moves, up to its end.</summary>
    private Dictionary<int, int> Moves(uint list)
    {
        var memory = _harness.Core.Memory;
        var moves = new Dictionary<int, int>();
        for (var address = list; memory.Read32(address) != 0xFFFF_FFFE; address += 4)
        {
            var first = memory.Read16(address);
            if ((first & 1) == 0)
                moves[first] = memory.Read16(address + 2);
        }

        return moves;
    }
}
