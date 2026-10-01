using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

/// <summary>Tests of the display with the AGA chipset: the palette, the fetch modes and the wide sprites.</summary>
public class AgaDisplayTests() : DisplayTestBench(aga: true)
{
    private const uint CopperList = 0x1_0000;
    private const uint Plane = 0x2_0000;
    private const uint Sprite = 0x3_0000;

    [Fact]
    public void ColorWrite_GoesToTheBankOfBplcon3_AndLoctSetsTheLowBits()
    {
        Write(0xDFF106, 0x2000); // BPLCON3: bank 1.
        Write(0xDFF180, 0x0F80); // COLOR00 of bank 1 is color 32.
        Write(0xDFF106, 0x2200); // Bank 1 with LOCT.
        Write(0xDFF180, 0x0123);

        Assert.Equal(0xF18203u, Chipset.Custom.Palette[32]);
        Assert.Equal(0u, Chipset.Custom.Palette[0]);
    }

    [Fact]
    public void FetchMode1_FetchesTwoWordsInEachUnit_AndTheNextRowFollowsTheLastWord()
    {
        // FMODE 1 in low resolution: units of 16 color clocks from $38 to $C8, 10 units of 2 words, 40 bytes. The data
        // of a unit shows 33 pixels after twice DDFSTRT: at $91.
        SetUp(0x2C91, fmode: 0x0001);
        Memory.Write8(Plane, 0x80);
        Memory.Write8(Plane + 40, 0x80);

        var frame = NextFrame();

        var x = X(0x91);
        Assert.Equal(Red, Pixel(frame, x, 0x2C));
        Assert.Equal(Black, Pixel(frame, x + 2, 0x2C));
        Assert.Equal(Red, Pixel(frame, x, 0x2D));
    }

    [Fact]
    public void WideSprite_Shows64Pixels_AndAgain256PixelsLater()
    {
        // A 64-pixel sprite: SPRxPOS at +0 and SPRxCTL at +8, then DATA (8 bytes) and DATB (8 bytes) for each line.
        Memory.Write16(Sprite, 0x3040); // VSTART $30, HSTART $80: the first pixel at $81.
        Memory.Write16(Sprite + 8, 0x3100); // VSTOP $31.
        Memory.Write16(Sprite + 16, 0x8000); // Pixel 0.
        Memory.Write16(Sprite + 22, 0x0001); // Pixel 63.
        SetUp(0x2C81, fmode: 0x000C, Move(0x120, Sprite >> 16), Move(0x122, Sprite & 0xFFFF), Move(0x1A2, 0x0F0));
        Write(0xDFF096, 0x8020); // DMACON: sprites

        NextFrame();
        var frame = NextFrame();

        Assert.Equal(Green, Pixel(frame, X(0x81), 0x30));
        Assert.Equal(Green, Pixel(frame, X(0x81 + 63), 0x30));
        Assert.Equal(Black, Pixel(frame, X(0x81 + 64), 0x30));
        Assert.Equal(Green, Pixel(frame, X(0x81 + 256), 0x30));
    }

    [Theory]
    [InlineData(0x10)] // Before the sprite DMA of the line.
    [InlineData(0x20)] // After it.
    public void SpritePositionWrite_OnTheStartLine_StartsTheSpriteOnThatLine(int horizontal)
    {
        // The control words start the sprite at line 0, which the sprite DMA never sees. The copper moves it to $30.
        Memory.Write16(Sprite, 0x0040);
        Memory.Write16(Sprite + 2, 0x3100);
        Memory.Write16(Sprite + 4, 0x8000);
        SetUp(0x2C81, fmode: 0, Move(0x120, Sprite >> 16), Move(0x122, Sprite & 0xFFFF), Move(0x1A2, 0x0F0),
            Wait(0x30, horizontal), Move(0x140, 0x3040));
        Write(0xDFF096, 0x8020); // DMACON: sprites

        NextFrame();
        var frame = NextFrame();

        Assert.Equal(Green, Pixel(frame, X(0x81), 0x30));
    }

    private void SetUp(ushort diwStart, ushort fmode, params uint[][] extra)
    {
        Write(0xDFF08E, diwStart); // DIWSTRT
        Write(0xDFF090, 0xF4C1); // DIWSTOP
        Write(0xDFF092, 0x38); // DDFSTRT
        Write(0xDFF094, 0xC8); // DDFSTOP
        Write(0xDFF100, 0x1200); // BPLCON0: 1 plane
        Write(0xDFF1FC, fmode);
        var list = new List<uint[]>
        {
            Move(0x0E0, Plane >> 16),
            Move(0x0E2, Plane & 0xFFFF),
            Move(0x180, 0x000),
            Move(0x182, 0xF00),
        };
        list.AddRange(extra);
        list.Add(End());
        WriteCopperList(CopperList, list.ToArray());
        Write32(0xDFF080, CopperList);
        Write(0xDFF096, 0x8380); // DMACON: DMA, bitplanes, copper
    }
}
