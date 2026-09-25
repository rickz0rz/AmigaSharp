using AmigaSharp.Runtime.Graphics;

namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>diskfont.library: loads fonts from FONTS: and adds them to the font list of graphics.library.</summary>
public class DiskFontLibrary(Core core) : AbstractLibrary
{
    private readonly Memory _memory = core.Memory;

    public override string Name => "diskfont.library";
    public override ushort Version => 40;
    public override short LowestOffset => -54;

    // font = OpenDiskFont(textAttr)
    // D0                  A0
    [LibraryFunctionOffset(-30)]
    public uint OpenDiskFont([A0] uint textAttr)
    {
        var graphics = core.Libraries.Instance<GraphicsLibrary>("graphics.library");
        var name = _memory.ReadCString(_memory.Read32(textAttr + TextAttrOffsets.Name));
        var ySize = _memory.Read16(textAttr + TextAttrOffsets.YSize);

        // A font with the same name and size that is already in memory.
        var font = graphics.Fonts.Find(graphics.FontList, name, ySize, exactSize: true);
        if (font == 0)
        {
            font = graphics.Fonts.LoadDiskFont(name, ySize);
            if (font != 0)
                graphics.AddFont(font);
            else
                font = graphics.Fonts.Find(graphics.FontList, name, ySize, exactSize: false);
        }

        if (font == 0)
        {
            core.Log.WriteLine($"OpenDiskFont: no font {name} {ySize} in FONTS:.");
            return 0;
        }

        var accessors = font + TextFontOffsets.Accessors;
        _memory.Write16(accessors, (ushort)(_memory.Read16(accessors) + 1));
        return font;
    }
}
