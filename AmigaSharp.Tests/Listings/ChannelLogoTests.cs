using System.Text;
using AmigaSharp.PrevueListings.Logos;
using AmigaSharp.Runtime.Graphics;

namespace AmigaSharp.Tests.Listings;

public sealed class ChannelLogoTests : IDisposable
{
    private readonly string _drive = Directory.CreateTempSubdirectory("AmigaSharp-logos-").FullName;

    public void Dispose() => Directory.Delete(_drive, recursive: true);

    [Fact]
    public void Png_IsReadAsRgba()
    {
        uint[] pixels = [0xFF102030, 0xFFFFFFFF, 0xFF000000, 0xFF405060];

        var image = PngImage.Read(Png.Encode(2, 2, pixels));

        Assert.Equal((2, 2), (image.Width, image.Height));
        Assert.Equal([0x10, 0x20, 0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], image.Pixels[..8]);
        Assert.Equal([0x40, 0x50, 0x60, 0xFF], image.Pixels[12..]);
    }

    [Fact]
    public void ByteRun1_PacksRunsAndLiterals()
    {
        using var output = new MemoryStream();

        IlbmImage.ByteRun1([1, 2, 3, 7, 7, 7, 7, 9], output);

        Assert.Equal([2, 1, 2, 3, unchecked((byte)-3), 7, 0, 9], output.ToArray());
    }

    [Fact]
    public void Logo_DoesNotUseTheGenlockColor_AndLeavesTheRightSideForTheText()
    {
        var station = new RgbaImage(4, 3, Enumerable.Repeat<byte[]>([200, 30, 30, 255], 12).SelectMany(p => p).ToArray());

        var (palette, pixels) = ChannelLogo.Render(station);

        Assert.Equal(32, palette.Count);
        Assert.DoesNotContain((byte)0, pixels);
        // Color 1 is white, the brightest color, for the text of ESQ.
        Assert.Equal(new AmigaColor(15, 15, 15), palette[1]);
        Assert.All(palette, color => Assert.True(color.R + color.G + color.B <= 45));
        // The right side is only navy (color 2), and the red of the station is on the card.
        var navy = (byte)2;
        for (var y = 0; y < ChannelLogo.Height; y++)
        {
            for (var x = 180; x < ChannelLogo.Width; x++)
                Assert.Equal(navy, pixels[y * ChannelLogo.Width + x]);
        }

        Assert.Contains(pixels, index => palette[index] is { R: >= 10, G: <= 4 });
    }

    [Fact]
    public void Write_MakesIffFiles_AndReplacesTheChannelLinesOfTheList()
    {
        File.WriteAllText(Path.Combine(_drive, "LOGO.LST"),
            "Logos/tvgsport.uv,\r\nLogos/Channels/OLD\r\nLogos/KSIN!\r\n", Encoding.Latin1);
        var png = Png.Encode(2, 2, [0xFF0000FF, 0xFF0000FF, 0xFF0000FF, 0xFF0000FF]);

        var count = ChannelLogos.Write(_drive, [("KTIVDT", png), ("WJBKDT", [1, 2, 3])], TextWriter.Null);

        Assert.Equal(1, count);
        Assert.Equal("Logos/tvgsport.uv,\r\nLogos/KSIN!\r\nLogos/Channels/KTIVDT\r\n",
            File.ReadAllText(Path.Combine(_drive, "LOGO.LST"), Encoding.Latin1));
        var file = File.ReadAllBytes(Path.Combine(_drive, "Logos", "Channels", "KTIVDT"));
        Assert.Equal("FORM", Encoding.ASCII.GetString(file, 0, 4));
        Assert.Equal("ILBM", Encoding.ASCII.GetString(file, 8, 4));
    }

    [Theory]
    [InlineData("KTIVDT", "KTIVDT")]
    [InlineData("WKAR-DT2", "WKARDT")]
    [InlineData("hbo hd", "HBOHD")]
    public void Name_IsTheSourceNameOfTheListings(string text, string name)
    {
        Assert.Equal(name, ChannelLogos.Name(text));
    }
}
