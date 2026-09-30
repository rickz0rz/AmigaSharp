namespace AmigaSharp.PrevueListings.Logos;

/// <summary>
/// Makes the picture of a channel logo for ESQ from an image of the station: a low-resolution picture of 320 by 240
/// pixels with 32 colors, as the logos of the Prevue drive. The station image is on a light card at the left, on the
/// navy of Prevue. The right part stays empty, because ESQ writes the call letters and the channel number there.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>
/// Color 0 is the genlock key: the video of the genlock shows where the picture has color 0. So no pixel has color 0.
/// </item>
/// <item>ESQ writes its text in the brightest color, so color 1 is white.</item>
/// <item>
/// A low-resolution pixel is wider than it is tall: 44 to 26, as in the logos of the drive. The station image is
/// narrower in pixels than in its file, so that it has its shape on the screen.
/// </item>
/// </list>
/// </remarks>
public static class ChannelLogo
{
    public const int Width = 320, Height = 240, Planes = 5;

    private const double PixelAspect = 44.0 / 26.0;
    private static readonly AmigaColor Black = new(0, 0, 0);
    private static readonly AmigaColor White = new(15, 15, 15);
    private static readonly AmigaColor Navy = new(0, 0, 4);
    private static readonly AmigaColor Card = new(14, 14, 14);

    // The card on the left, in pixels of the logo, and the margin of the station image in it.
    private const int CardLeft = 12, CardTop = 30, CardRight = 130, CardBottom = 210, Margin = 8;

    /// <summary>Makes the ILBM file of the channel logo of a station image.</summary>
    public static byte[] Create(RgbaImage station)
    {
        var (palette, pixels) = Render(station);
        return IlbmImage.Write(Width, Height, Planes, palette, pixels);
    }

    /// <summary>The palette and the palette index of each pixel of the channel logo.</summary>
    public static (List<AmigaColor> Palette, byte[] Pixels) Render(RgbaImage station)
    {
        // The card with the station image in its middle, in 8-bit RGB.
        var cardWidth = CardRight - CardLeft;
        var cardHeight = CardBottom - CardTop;
        var card = new int[cardWidth * cardHeight * 3];
        var (cardR, cardG, cardB) = Card.Rgb;
        for (var i = 0; i < cardWidth * cardHeight; i++)
            (card[i * 3], card[i * 3 + 1], card[i * 3 + 2]) = (cardR, cardG, cardB);

        // The image fits in the card without the margin, with its shape on the screen.
        var boxWidth = (cardWidth - 2 * Margin) * PixelAspect;
        var boxHeight = (double)(cardHeight - 2 * Margin);
        var scale = Math.Min(boxWidth / station.Width, boxHeight / station.Height);
        var imageWidth = Math.Max(1, (int)Math.Round(station.Width * scale / PixelAspect));
        var imageHeight = Math.Max(1, (int)Math.Round(station.Height * scale));
        var left = (cardWidth - imageWidth) / 2;
        var top = (cardHeight - imageHeight) / 2;
        var scaled = Scale(station, imageWidth, imageHeight);
        for (var y = 0; y < imageHeight; y++)
        {
            for (var x = 0; x < imageWidth; x++)
            {
                var source = (y * imageWidth + x) * 4;
                var alpha = scaled[source + 3] / 255.0;
                var target = ((top + y) * cardWidth + left + x) * 3;
                for (var c = 0; c < 3; c++)
                    card[target + c] = (int)Math.Round(scaled[source + c] * alpha + card[target + c] * (1 - alpha));
            }
        }

        var palette = new List<AmigaColor> { Black, White, Navy, Card };
        foreach (var color in MedianCut(card, 32 - palette.Count))
        {
            if (!palette.Contains(color))
                palette.Add(color);
        }

        while (palette.Count < 32)
            palette.Add(Black);

        var pixels = new byte[Width * Height];
        Array.Fill(pixels, (byte)palette.IndexOf(Navy));
        Dither(card, cardWidth, cardHeight, palette);
        for (var y = 0; y < cardHeight; y++)
        {
            for (var x = 0; x < cardWidth; x++)
                pixels[(CardTop + y) * Width + CardLeft + x] = (byte)card[(y * cardWidth + x) * 3];
        }

        return (palette, pixels);
    }

    /// <summary>Scales an image to a size, with the average of the source pixels that each pixel covers.</summary>
    private static byte[] Scale(RgbaImage image, int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var y0 = y * image.Height / height;
            var y1 = Math.Max(y0 + 1, (y + 1) * image.Height / height);
            for (var x = 0; x < width; x++)
            {
                var x0 = x * image.Width / width;
                var x1 = Math.Max(x0 + 1, (x + 1) * image.Width / width);
                // Colors are weighted by their alpha, so that transparent pixels do not darken the edges.
                double r = 0, g = 0, b = 0, a = 0;
                var count = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = (sy * image.Width + sx) * 4;
                        var alpha = image.Pixels[i + 3];
                        r += image.Pixels[i] * alpha;
                        g += image.Pixels[i + 1] * alpha;
                        b += image.Pixels[i + 2] * alpha;
                        a += alpha;
                        count++;
                    }
                }

                var target = (y * width + x) * 4;
                if (a > 0)
                    (result[target], result[target + 1], result[target + 2]) =
                        ((byte)(r / a), (byte)(g / a), (byte)(b / a));
                result[target + 3] = (byte)(a / count);
            }
        }

        return result;
    }

    /// <summary>
    /// Chooses up to a number of colors for the pixels (8-bit RGB triples) with a median cut: the box of colors with
    /// the most pixels is cut in two at the median of its widest part, until there are enough boxes.
    /// </summary>
    private static List<AmigaColor> MedianCut(int[] rgb, int count)
    {
        var colors = new List<(int R, int G, int B)>(rgb.Length / 3);
        for (var i = 0; i < rgb.Length; i += 3)
            colors.Add((rgb[i], rgb[i + 1], rgb[i + 2]));
        var boxes = new List<List<(int R, int G, int B)>> { colors };
        while (boxes.Count < count)
        {
            var box = boxes.Where(b => b.Count > 1 && Range(b) > 0).OrderByDescending(b => b.Count * Range(b))
                .FirstOrDefault();
            if (box == null)
                break;
            var (dr, dg, db) = (Spread(box, c => c.R), Spread(box, c => c.G), Spread(box, c => c.B));
            Func<(int R, int G, int B), int> key = dr >= dg && dr >= db ? c => c.R : dg >= db ? c => c.G : c => c.B;
            var sorted = box.OrderBy(key).ToList();
            boxes.Remove(box);
            boxes.Add(sorted[..(sorted.Count / 2)]);
            boxes.Add(sorted[(sorted.Count / 2)..]);
        }

        return boxes.Where(b => b.Count > 0).Select(b => AmigaColor.From(
                (int)b.Average(c => c.R), (int)b.Average(c => c.G), (int)b.Average(c => c.B)))
            .Distinct().ToList();

        static int Spread(List<(int R, int G, int B)> box, Func<(int R, int G, int B), int> part) =>
            box.Max(part) - box.Min(part);
        static int Range(List<(int R, int G, int B)> box) =>
            Math.Max(Spread(box, c => c.R), Math.Max(Spread(box, c => c.G), Spread(box, c => c.B)));
    }

    /// <summary>
    /// Changes each pixel to the nearest color of the palette (not color 0), with Floyd-Steinberg dithering. The first
    /// part of each pixel gets the palette index.
    /// </summary>
    private static void Dither(int[] rgb, int width, int height, List<AmigaColor> palette)
    {
        var values = palette.Select(c => c.Rgb).ToArray();
        var error = new double[rgb.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                var r = Math.Clamp(rgb[i] + error[i], 0, 255);
                var g = Math.Clamp(rgb[i + 1] + error[i + 1], 0, 255);
                var b = Math.Clamp(rgb[i + 2] + error[i + 2], 0, 255);
                var best = 1;
                var bestDistance = double.MaxValue;
                for (var p = 1; p < values.Length; p++)
                {
                    var (pr, pg, pb) = values[p];
                    var distance = (r - pr) * (r - pr) * 0.3 + (g - pg) * (g - pg) * 0.59 + (b - pb) * (b - pb) * 0.11;
                    if (distance < bestDistance)
                        (best, bestDistance) = (p, distance);
                }

                var (cr, cg, cb) = values[best];
                Spread(x + 1, y, 7 / 16.0);
                Spread(x - 1, y + 1, 3 / 16.0);
                Spread(x, y + 1, 5 / 16.0);
                Spread(x + 1, y + 1, 1 / 16.0);
                rgb[i] = best;

                void Spread(int sx, int sy, double part)
                {
                    if (sx < 0 || sx >= width || sy >= height)
                        return;
                    var j = (sy * width + sx) * 3;
                    error[j] += (r - cr) * part;
                    error[j + 1] += (g - cg) * part;
                    error[j + 2] += (b - cb) * part;
                }
            }
        }
    }
}
