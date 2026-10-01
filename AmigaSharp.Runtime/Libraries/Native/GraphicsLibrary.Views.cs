using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Graphics;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// The View functions of graphics.library: a program describes its display with a View and ViewPorts, and MrgCop makes
/// the copper list of the display from them. LoadView then shows that list.
/// </summary>
/// <remarks>
/// MrgCop makes, for each ViewPort that shows, a wait for its first line, the registers of the display (window, data
/// fetch, BPLCON0 to BPLCON2, modulos and plane pointers) and its colors. MakeVPort does nothing: MrgCop reads the
/// ViewPorts. User copper lists (UCopIns), interlace (a second list for the short frame) and sprites are not done.
/// </remarks>
public partial class GraphicsLibrary
{
    // The copper lists that MrgCop made, by their cprlist, with their size. FreeCprList and the next MrgCop free them.
    private readonly Dictionary<uint, uint> _copperLists = [];

    // The data words of the color moves of each ViewPort in the copper list, so that SetRGB4 can change them.
    private readonly Dictionary<uint, Dictionary<int, uint>> _colorMoves = [];

    // InitView(view)
    //          A1
    [LibraryFunctionOffset(-360)]
    public void InitView([A1] uint view)
    {
        _memory.WriteBytes(view, new byte[ViewOffsets.Size]);
        // The standard position of the display: line $2C, low-resolution pixel $81.
        _memory.Write16(view + ViewOffsets.DxOffset, 0x81);
        _memory.Write16(view + ViewOffsets.DyOffset, 0x2C);
    }

    // InitVPort(vp)
    //           A0
    [LibraryFunctionOffset(-204)]
    public void InitVPort([A0] uint viewPort) => _memory.WriteBytes(viewPort, new byte[ViewPortOffsets.Size]);

    // MakeVPort(view, vp)
    //           A0    A1
    // MrgCop makes the copper list of all the ViewPorts, so this function has nothing to do.
    [LibraryFunctionOffset(-216)]
    public void MakeVPort([A0] uint view, [A1] uint viewPort)
    {
    }

    // MrgCop(view)
    //        A1
    [LibraryFunctionOffset(-210)]
    public void MrgCop([A1] uint view)
    {
        var old = _memory.Read32(view + ViewOffsets.LofCprList);
        if (_copperLists.Remove(old, out var oldSize))
            core.Allocator.Free(old, oldSize);

        var instructions = new List<(ushort Register, ushort Value, uint ViewPort, int Color)>();
        for (var viewPort = _memory.Read32(view + ViewOffsets.ViewPort); viewPort != 0;
             viewPort = _memory.Read32(viewPort + ViewPortOffsets.Next))
        {
            if ((_memory.Read16(viewPort + ViewPortOffsets.Modes) & ViewModes.Hide) == 0)
                AddViewPort(view, viewPort, instructions);
        }

        // The cprlist and the instructions, with the end of the list: WAIT for a line that never comes.
        var size = 12 + (uint)(instructions.Count + 1) * 4;
        var list = core.Allocator.Allocate(size, MemoryFlags.Chip | MemoryFlags.Clear);
        _copperLists[list] = size;
        var start = list + 12;
        _memory.Write32(list + CprListOffsets.Start, start);
        _memory.Write16(list + 8, (ushort)(instructions.Count + 1));
        var address = start;
        foreach (var (register, value, viewPort, color) in instructions)
        {
            _memory.Write16(address, register);
            _memory.Write16(address + 2, value);
            if (color >= 0)
            {
                if (!_colorMoves.TryGetValue(viewPort, out var moves))
                    _colorMoves[viewPort] = moves = [];
                moves[color] = address + 2;
            }

            address += 4;
        }

        _memory.Write32(address, 0xFFFF_FFFE);
        _memory.Write32(view + ViewOffsets.LofCprList, list);
    }

    private void AddViewPort(uint view, uint viewPort, List<(ushort, ushort, uint, int)> instructions)
    {
        void Move(int register, int value, int color = -1) =>
            instructions.Add(((ushort)register, (ushort)value, viewPort, color));

        var viewModes = _memory.Read16(view + ViewOffsets.Modes);
        var modes = _memory.Read16(viewPort + ViewPortOffsets.Modes);
        var hires = (modes & ViewModes.Hires) != 0;
        var lace = ((modes | viewModes) & ViewModes.Lace) != 0;
        var rasInfo = _memory.Read32(viewPort + ViewPortOffsets.RasInfo);
        var bitMap = rasInfo == 0 ? 0 : _memory.Read32(rasInfo + RasInfoOffsets.BitMap);
        var depth = bitMap == 0 ? 0 : Math.Min((int)_memory.Read8(bitMap + BitMapOffsets.Depth), 6);
        var bytesPerRow = bitMap == 0 ? 0 : _memory.Read16(bitMap + BitMapOffsets.BytesPerRow);

        // The position and the size in low-resolution pixels and in lines.
        var width = (short)_memory.Read16(viewPort + ViewPortOffsets.DWidth);
        var height = (short)_memory.Read16(viewPort + ViewPortOffsets.DHeight);
        var dx = (short)_memory.Read16(viewPort + ViewPortOffsets.DxOffset);
        var dy = (short)_memory.Read16(viewPort + ViewPortOffsets.DyOffset);
        var left = (short)_memory.Read16(view + ViewOffsets.DxOffset) + (hires ? dx / 2 : dx);
        var top = (short)_memory.Read16(view + ViewOffsets.DyOffset) + (lace ? dy / 2 : dy);
        var lowResWidth = hires ? width / 2 : width;
        var lines = lace ? (height + 1) / 2 : height;

        // A ViewPort below the first one starts at its line.
        if (instructions.Count > 0)
            instructions.Add(((ushort)((Math.Clamp(top - 1, 0, 255) << 8) | 0x01), 0xFFFE, viewPort, -1));

        var bplcon0 = depth << 12 | 0x0200;
        if (hires)
            bplcon0 |= 0x8000;
        if (lace)
            bplcon0 |= 0x0004;
        if ((modes & ViewModes.Ham) != 0)
            bplcon0 |= 0x0800;
        if ((modes & ViewModes.DualPlayfield) != 0)
            bplcon0 |= 0x0400;
        if ((modes & ViewModes.GenlockVideo) != 0)
            bplcon0 |= 0x0002;

        // The data fetch: 16 pixels for each word, from the window start.
        var words = Math.Max((hires ? width : lowResWidth) / 16, 1);
        var fetchStart = hires ? ((left - 9) / 2) & 0xFC : ((left - 17) / 2) & 0xF8;
        var fetchStop = hires ? fetchStart + 4 * (words - 2) : fetchStart + 8 * (words - 1);
        var modulo = bytesPerRow - words * 2 + (lace ? bytesPerRow : 0);
        var bottom = top + lines;

        Move(CustomRegister.Diwstrt, (top & 0xFF) << 8 | (left & 0xFF));
        Move(CustomRegister.Diwstop, (bottom & 0xFF) << 8 | ((left + lowResWidth) & 0xFF));
        Move(CustomRegister.Ddfstrt, fetchStart);
        Move(CustomRegister.Ddfstop, fetchStop);
        Move(CustomRegister.Bplcon0, bplcon0);
        Move(CustomRegister.Bplcon1, 0);
        Move(CustomRegister.Bplcon2, 0x0024);
        Move(CustomRegister.Bpl1mod, modulo);
        Move(CustomRegister.Bpl2mod, modulo);

        var rx = rasInfo == 0 ? 0 : (short)_memory.Read16(rasInfo + RasInfoOffsets.RxOffset);
        var ry = rasInfo == 0 ? 0 : (short)_memory.Read16(rasInfo + RasInfoOffsets.RyOffset);
        var offset = ry * bytesPerRow + rx / 16 * 2;
        for (var plane = 0; plane < depth; plane++)
        {
            var pointer = (uint)(_memory.Read32(bitMap + BitMapOffsets.Planes + (uint)plane * 4) + offset);
            Move(CustomRegister.Bpl1pt + plane * 4, (int)(pointer >> 16));
            Move(CustomRegister.Bpl1pt + plane * 4 + 2, (int)(pointer & 0xFFFF));
        }

        var colorMap = _memory.Read32(viewPort + ViewPortOffsets.ColorMap);
        if (colorMap == 0)
            return;
        var table = _memory.Read32(colorMap + ColorMapOffsets.ColorTable);
        var count = Math.Min((int)_memory.Read16(colorMap + ColorMapOffsets.Count), 32);
        for (var color = 0; color < count; color++)
            Move(CustomRegister.Color00 + color * 2, _memory.Read16(table + (uint)color * 2), color);
    }

    // FreeCprList(cprlist)
    //             A0
    [LibraryFunctionOffset(-564)]
    public void FreeCprList([A0] uint list)
    {
        if (_copperLists.Remove(list, out var size))
            core.Allocator.Free(list, size);
    }

    // FreeVPortCopLists(vp)
    //                   A0
    // MrgCop keeps the copper list of the View, so a ViewPort has no lists of its own.
    [LibraryFunctionOffset(-540)]
    public void FreeVPortCopLists([A0] uint viewPort) => _colorMoves.Remove(viewPort);

    // cm = GetColorMap(entries)
    // D0               D0
    [LibraryFunctionOffset(-570)]
    public uint GetColorMap([D0] uint entries)
    {
        var count = (ushort)Math.Max(entries, 1);
        var colorMap = core.Allocator.Allocate(ColorMapOffsets.Size, MemoryFlags.Public | MemoryFlags.Clear);
        var table = core.Allocator.Allocate((uint)count * 2, MemoryFlags.Public | MemoryFlags.Clear);
        if (colorMap == 0 || table == 0)
            return 0;
        _memory.Write16(colorMap + ColorMapOffsets.Count, count);
        _memory.Write32(colorMap + ColorMapOffsets.ColorTable, table);
        return colorMap;
    }

    // FreeColorMap(colormap)
    //              A0
    [LibraryFunctionOffset(-576)]
    public void FreeColorMap([A0] uint colorMap)
    {
        if (colorMap == 0)
            return;
        var count = _memory.Read16(colorMap + ColorMapOffsets.Count);
        core.Allocator.Free(_memory.Read32(colorMap + ColorMapOffsets.ColorTable), (uint)count * 2);
        core.Allocator.Free(colorMap, ColorMapOffsets.Size);
    }

    // SetRGB4(vp, n, r, g, b)
    //         A0  D0 D1 D2 D3
    [LibraryFunctionOffset(-288)]
    public void SetRgb4([A0] uint viewPort, [D0] short color, [D1] byte red, [D2] byte green, [D3] byte blue) =>
        SetColor(viewPort, color, (ushort)((red & 15) << 8 | (green & 15) << 4 | (blue & 15)));

    // LoadRGB4(vp, colors, count)
    //          A0  A1      D0
    [LibraryFunctionOffset(-192)]
    public void LoadRgb4([A0] uint viewPort, [A1] uint colors, [D0] short count)
    {
        for (var color = 0; color < count; color++)
            SetColor(viewPort, color, _memory.Read16(colors + (uint)color * 2));
    }

    // SetRGB4CM(colormap, n, r, g, b)
    //           A0        D0 D1 D2 D3
    [LibraryFunctionOffset(-630)]
    public void SetRgb4Cm([A0] uint colorMap, [D0] short color, [D1] byte red, [D2] byte green, [D3] byte blue) =>
        SetColorMapEntry(colorMap, color, (ushort)((red & 15) << 8 | (green & 15) << 4 | (blue & 15)));

    /// <summary>
    /// Sets a color of a ViewPort: in its ColorMap, and in the copper list of the View if MrgCop made it. Then the
    /// display shows the new color in the next frame.
    /// </summary>
    private void SetColor(uint viewPort, int color, ushort value)
    {
        if (viewPort == 0 || color < 0)
            return;
        SetColorMapEntry(_memory.Read32(viewPort + ViewPortOffsets.ColorMap), color, value);
        if (_colorMoves.TryGetValue(viewPort, out var moves) && moves.TryGetValue(color, out var address))
            _memory.Write16(address, value);
    }

    private void SetColorMapEntry(uint colorMap, int color, ushort value)
    {
        if (colorMap == 0 || color < 0 || color >= _memory.Read16(colorMap + ColorMapOffsets.Count))
            return;
        _memory.Write16(_memory.Read32(colorMap + ColorMapOffsets.ColorTable) + (uint)color * 2, (ushort)(value & 0x0FFF));
    }
}
