namespace AmigaSharp.Runtime.Graphics;

// Field offsets of the graphics structures, from the V40 include files.

/// <summary><c>struct BitMap</c> (graphics/gfx.h).</summary>
public static class BitMapOffsets
{
    public const uint BytesPerRow = 0;
    public const uint Rows = 2;
    public const uint Flags = 4;
    public const uint Depth = 5;
    public const uint Planes = 8;
    public const uint Size = 40;
    public const int MaximumDepth = 8;
}

/// <summary><c>struct RastPort</c> (graphics/rastport.h).</summary>
public static class RastPortOffsets
{
    public const uint Layer = 0;
    public const uint BitMap = 4;
    public const uint AreaPattern = 8;
    public const uint TmpRas = 12;
    public const uint AreaInfo = 16;
    public const uint GelsInfo = 20;
    public const uint Mask = 24;
    public const uint FgPen = 25;
    public const uint BgPen = 26;
    public const uint AOlPen = 27;
    public const uint DrawMode = 28;
    public const uint AreaPatternSize = 29;
    public const uint LinePatternCount = 30;
    public const uint Flags = 32;
    public const uint LinePattern = 34;
    public const uint X = 36;
    public const uint Y = 38;
    public const uint Minterms = 40;
    public const uint PenWidth = 48;
    public const uint PenHeight = 50;
    public const uint Font = 52;
    public const uint AlgoStyle = 56;
    public const uint TxFlags = 57;
    public const uint TxHeight = 58;
    public const uint TxWidth = 60;
    public const uint TxBaseline = 62;
    public const uint TxSpacing = 64;
    public const uint User = 66;
    public const uint Size = 100;
}

/// <summary>The draw modes of a RastPort (rp_DrawMode).</summary>
[Flags]
public enum DrawMode : byte
{
    /// <summary>Draws the foreground pen only.</summary>
    Jam1 = 0,

    /// <summary>Draws the foreground pen and the background pen.</summary>
    Jam2 = 1,

    /// <summary>Inverts the bits of the destination.</summary>
    Complement = 2,

    /// <summary>Changes the pixels that the pattern or the glyph does not cover.</summary>
    InverseVideo = 4,
}

/// <summary><c>struct TextAttr</c> (graphics/text.h).</summary>
public static class TextAttrOffsets
{
    public const uint Name = 0;
    public const uint YSize = 4;
    public const uint Style = 6;
    public const uint Flags = 7;
    public const uint Size = 8;
}

/// <summary><c>struct TextFont</c> (graphics/text.h). It starts with a Message.</summary>
public static class TextFontOffsets
{
    public const uint YSize = 20;
    public const uint Style = 22;
    public const uint Flags = 23;
    public const uint XSize = 24;
    public const uint Baseline = 26;
    public const uint BoldSmear = 28;
    public const uint Accessors = 30;
    public const uint LoChar = 32;
    public const uint HiChar = 33;
    public const uint CharData = 34;
    public const uint Modulo = 38;
    public const uint CharLoc = 40;
    public const uint CharSpace = 44;
    public const uint CharKern = 48;
    public const uint Size = 52;

    /// <summary>tf_Style: the font is a ColorTextFont.</summary>
    public const byte ColorFontStyle = 1 << 6;

    /// <summary>tf_Flags: the font is in ROM.</summary>
    public const byte RomFont = 1 << 0;

    /// <summary>tf_Flags: the font is from a disk.</summary>
    public const byte DiskFont = 1 << 1;

    /// <summary>tf_Flags: the characters have different widths.</summary>
    public const byte Proportional = 1 << 5;

    public const byte NodeTypeFont = 12;
}

/// <summary>
/// <c>struct ColorTextFont</c> (graphics/text.h): a TextFont with more than one plane. Each plane has its own
/// CharData. The other fields are the same as in the TextFont.
/// </summary>
public static class ColorTextFontOffsets
{
    public const uint Flags = 52;
    public const uint Depth = 54;
    public const uint FgColor = 55;
    public const uint Low = 56;
    public const uint High = 57;
    public const uint PlanePick = 58;
    public const uint PlaneOnOff = 59;
    public const uint CharData = 64;
    public const int MaximumDepth = 8;

    /// <summary>ctf_Flags: the pixels of color ctf_FgColor get the foreground pen of the RastPort.</summary>
    public const ushort MapColor = 1 << 0;
}

/// <summary><c>struct View</c> (graphics/view.h).</summary>
public static class ViewOffsets
{
    public const uint ViewPort = 0;
    public const uint LofCprList = 4;
    public const uint ShfCprList = 8;
}

/// <summary><c>struct cprlist</c> (graphics/copper.h).</summary>
public static class CprListOffsets
{
    public const uint Next = 0;
    public const uint Start = 4;
}

/// <summary><c>struct GfxBase</c> (graphics/gfxbase.h).</summary>
public static class GfxBaseOffsets
{
    public const uint ActiView = 34;
    public const uint CopInit = 38;
    public const uint LofList = 50;
    public const uint TextFonts = 140;
    public const uint DefaultFont = 154;
    public const uint Modes = 158;
    public const uint VBlank = 160;
    public const uint SystemBplcon0 = 164;
    public const uint DisplayFlags = 206;
    public const uint MaxDisplayRow = 212;
    public const uint MaxDisplayColumn = 214;
    public const uint NormalDisplayRows = 216;
    public const uint NormalDisplayColumns = 218;
    public const uint NormalDpmX = 220;
    public const uint NormalDpmY = 222;
    public const uint MicrosPerLine = 232;
    public const uint MinDisplayColumn = 234;
    public const uint ChipRevBits0 = 236;
    public const uint Size = 552;

    /// <summary>DisplayFlags: the display is NTSC.</summary>
    public const ushort Ntsc = 1;

    /// <summary>ChipRevBits0: the ECS Agnus (GFXF_HR_AGNUS) and the ECS Denise (GFXF_HR_DENISE).</summary>
    public const byte EcsChips = 0x01 | 0x02;
}
