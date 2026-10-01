using System.Text;
using AmigaSharp.Runtime.Exec;
using AmigaSharp.Runtime.Graphics;

namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>
/// intuition.library. The runtime has one screen, the Workbench screen, and one window on it, the Shell window of
/// the program. Programs read them from IntuitionBase. The requesters and the alerts go to the log, and they return
/// the answer of the right button ("Cancel").
/// </summary>
public class IntuitionLibrary(Core core) : AbstractLibrary
{
    /// <summary>The size of the Workbench screen: NTSC high resolution, 4 colors.</summary>
    public const int ScreenWidth = 640;
    public const int ScreenHeight = 200;
    public const int ScreenDepth = 2;

    // IntuiText offsets (intuition/intuition.h).
    private const uint IntuiTextText = 12;
    private const uint IntuiTextNext = 16;

    private const uint ScreenSize = 400;
    private const uint WindowSize = 160;

    private readonly Memory _memory = core.Memory;

    public override string Name => "intuition.library";
    public override ushort Version => 40;
    public override ushort PositiveSize => (ushort)IntuitionBaseOffsets.Size;
    public override short LowestOffset => -828;

    /// <summary>The Workbench screen.</summary>
    public uint WorkbenchScreen { get; private set; }

    /// <summary>The Shell window of the program.</summary>
    public uint ShellWindow { get; private set; }

    public override void Initialize()
    {
        var graphics = core.Libraries.Instance<GraphicsLibrary>("graphics.library");

        // The screen and its bitmap. The planes are one block of chip memory, one plane after the other, as
        // intuition allocates them.
        var screen = core.AllocateSystem(ScreenSize);
        const int planeSize = ScreenWidth / 8 * ScreenHeight;
        var planes = core.Allocator.Allocate(planeSize * ScreenDepth, MemoryFlags.Chip | MemoryFlags.Clear);
        var bitMap = screen + ScreenOffsets.BitMap;
        graphics.InitBitMap(bitMap, ScreenDepth, ScreenWidth, ScreenHeight);
        for (var plane = 0; plane < ScreenDepth; plane++)
            _memory.Write32(bitMap + BitMapOffsets.Planes + (uint)plane * 4, planes + (uint)(plane * planeSize));

        _memory.Write16(screen + ScreenOffsets.Width, ScreenWidth);
        _memory.Write16(screen + ScreenOffsets.Height, ScreenHeight);
        _memory.Write16(screen + ScreenOffsets.Flags, ScreenOffsets.WorkbenchScreen);
        var title = core.AllocateSystem(Encoding.Latin1.GetBytes("Workbench Screen\0"));
        _memory.Write32(screen + ScreenOffsets.Title, title);
        _memory.Write32(screen + ScreenOffsets.DefaultTitle, title);
        _memory.Write8(screen + ScreenOffsets.BarHeight, 10);
        _memory.Write16(screen + ScreenOffsets.ViewPort + ViewPortOffsets.DWidth, ScreenWidth);
        _memory.Write16(screen + ScreenOffsets.ViewPort + ViewPortOffsets.DHeight, ScreenHeight);
        _memory.Write16(screen + ScreenOffsets.ViewPort + ViewPortOffsets.Modes, ViewModes.Hires);

        var rastPort = screen + ScreenOffsets.RastPort;
        graphics.InitRastPort(rastPort);
        _memory.Write32(rastPort + RastPortOffsets.BitMap, bitMap);

        // The Shell window covers the screen below the title bar.
        var window = core.AllocateSystem(WindowSize);
        _memory.Write16(window + WindowOffsets.TopEdge, 11);
        _memory.Write16(window + WindowOffsets.Width, ScreenWidth);
        _memory.Write16(window + WindowOffsets.Height, ScreenHeight - 11);
        _memory.Write32(window + WindowOffsets.RPort, rastPort);
        _memory.Write32(window + WindowOffsets.WScreen, screen);
        _memory.Write32(screen + ScreenOffsets.FirstWindow, window);

        _memory.Write32(Base + IntuitionBaseOffsets.ActiveWindow, window);
        _memory.Write32(Base + IntuitionBaseOffsets.ActiveScreen, screen);
        _memory.Write32(Base + IntuitionBaseOffsets.FirstScreen, screen);
        WorkbenchScreen = screen;
        ShellWindow = window;
    }

    // response = DisplayAlert(alertNumber, string, height)
    // D0                      D0           A0      D1
    [LibraryFunctionOffset(-90)]
    public int DisplayAlert([D0] uint number, [A0] uint text, [D1] ushort height)
    {
        core.Log.WriteLine($"DisplayAlert ${number:X8}: {AlertText(text)}");
        return 0;
    }

    // SizeWindow(window, dx, dy)
    //            A0      D0  D1
    [LibraryFunctionOffset(-288)]
    public void SizeWindow([A0] uint window, [D0] short dx, [D1] short dy)
    {
        _memory.Write16(window + WindowOffsets.Width, (ushort)(_memory.Read16(window + WindowOffsets.Width) + dx));
        _memory.Write16(window + WindowOffsets.Height, (ushort)(_memory.Read16(window + WindowOffsets.Height) + dy));
    }

    // response = AutoRequest(window, body, posText, negText, pFlag, nFlag, width, height)
    // D0                     A0      A1    A2       A3       D0     D1     D2     D3
    [LibraryFunctionOffset(-348)]
    public int AutoRequest([A0] uint window, [A1] uint body, [A2] uint positive, [A3] uint negative)
    {
        core.Log.WriteLine($"AutoRequest: {IntuiText(body)} [{IntuiText(positive)}] [{IntuiText(negative)}]");
        return 0;
    }

    // RemakeDisplay()
    [LibraryFunctionOffset(-384)]
    public int RemakeDisplay() => 0;

    /// <summary>The text of an IntuiText and the IntuiTexts after it.</summary>
    private string IntuiText(uint text)
    {
        var parts = new List<string>();
        for (var node = text; node != 0 && parts.Count < 32; node = _memory.Read32(node + IntuiTextNext))
        {
            var pointer = _memory.Read32(node + IntuiTextText);
            if (pointer != 0)
                parts.Add(_memory.ReadCString(pointer));
        }

        return string.Join(" / ", parts);
    }

    /// <summary>
    /// The text of an alert: each part is a word for x, a byte for y, a C string, and a byte that is not zero if
    /// another part follows.
    /// </summary>
    private string AlertText(uint text)
    {
        var parts = new StringBuilder();
        var address = text;
        while (text != 0 && parts.Length < 1000)
        {
            address += 3;
            var bytes = _memory.ReadCStringBytes(address);
            parts.Append(Encoding.Latin1.GetString(bytes)).Append(' ');
            address += (uint)bytes.Length + 1;
            if (_memory.Read8(address++) == 0)
                break;
        }

        return parts.ToString().Trim();
    }
}

/// <summary><c>struct IntuitionBase</c> (intuition/intuitionbase.h).</summary>
public static class IntuitionBaseOffsets
{
    public const uint ViewLord = 34;
    public const uint ActiveWindow = 52;
    public const uint ActiveScreen = 56;
    public const uint FirstScreen = 60;
    public const uint Size = 80;
}

/// <summary><c>struct Screen</c> (intuition/screens.h).</summary>
public static class ScreenOffsets
{
    public const uint NextScreen = 0;
    public const uint FirstWindow = 4;
    public const uint LeftEdge = 8;
    public const uint TopEdge = 10;
    public const uint Width = 12;
    public const uint Height = 14;
    public const uint Flags = 20;
    public const uint Title = 22;
    public const uint DefaultTitle = 26;
    public const uint BarHeight = 30;
    public const uint Font = 40;
    public const uint ViewPort = 44;
    public const uint RastPort = 84;
    public const uint BitMap = 184;

    /// <summary>Flags: the screen is the Workbench screen (WBENCHSCREEN).</summary>
    public const ushort WorkbenchScreen = 1;
}

/// <summary><c>struct Window</c> (intuition/intuition.h).</summary>
public static class WindowOffsets
{
    public const uint LeftEdge = 4;
    public const uint TopEdge = 6;
    public const uint Width = 8;
    public const uint Height = 10;
    public const uint Flags = 24;
    public const uint Title = 32;
    public const uint WScreen = 46;
    public const uint RPort = 50;
}
