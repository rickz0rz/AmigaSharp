namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// The video standard of the Amiga: NTSC or PAL. The custom chips and the CIAs run from its clocks, and the beam has
/// its number of lines.
/// </summary>
/// <param name="ColorClockHz">The color clock. The custom chips count their time in color clocks.</param>
/// <param name="LinesPerFrame">The lines of a short frame. A long frame of interlace has one more line.</param>
/// <param name="FirstLine">The first line in the picture of the display.</param>
/// <param name="PictureLines">The number of lines in the picture of the display.</param>
/// <param name="EClockHz">The E clock of the CIAs: a tenth of the CPU clock, and a fifth of the color clock.</param>
/// <param name="AgnusIdBit">The bit of the Agnus ID in VPOSR that tells NTSC ($10) or PAL (0).</param>
public sealed record VideoStandard(string Name, double ColorClockHz, int LinesPerFrame, int FirstLine, int PictureLines,
    double EClockHz, int AgnusIdBit)
{
    /// <summary>NTSC: 262 lines, about 60 frames each second. The picture has lines 21 to 260.</summary>
    public static readonly VideoStandard Ntsc = new("NTSC", 3_579_545.0, 262, 21, 240, 715_909.09, 0x10);

    /// <summary>PAL: 312 lines, about 50 frames each second. The picture has lines 26 to 311.</summary>
    public static readonly VideoStandard Pal = new("PAL", 3_546_895.0, 312, 26, 286, 709_379.0, 0);

    /// <summary>The number of frames each second.</summary>
    public double FramesPerSecond => ColorClockHz / (Beam.ColorClocksPerLine * LinesPerFrame);
}
