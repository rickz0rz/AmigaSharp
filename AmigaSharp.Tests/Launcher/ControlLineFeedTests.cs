using System.Text;
using System.Text.Json;
using AmigaSharp.Launcher;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Launcher;

public class ControlLineFeedTests
{
    [Fact]
    public void Packet_IsTheType_TheBody_ACr_AndTheXorOfAllOfThem()
    {
        var packet = ControlLineFeed.Packet(1, "1*");

        Assert.Equal([0x01, 0x31, 0x2A, 0x0D, 0x01 ^ 0x31 ^ 0x2A ^ 0x0D], packet);
    }

    [Theory]
    [InlineData(0, "x")]
    [InlineData(23, "x")]
    [InlineData(1, "a\rb")]
    [InlineData(1, "☃")]
    public void Packet_RefusesABadTypeOrBody(int type, string body)
    {
        Assert.Throws<FormatException>(() => ControlLineFeed.Packet(type, body));
    }

    [Fact]
    public void PromoForTheRightBox_IsTheTitle_ThenTheSearch()
    {
        var packets = ControlLineFeed.Promo(Json("""{"title": "Seinfeld"}"""));

        Assert.Equal([(17, "Seinfeld"), (1, "1*")], packets.Select(Decode));
    }

    [Fact]
    public void PromoForTwoBoxes_HasTheBrushes_TheOrder_AndTheTwoTitles()
    {
        var packets = ControlLineFeed.Promo(Json("""
            {"right": {"title": "Bob’s Burgers", "channels": "KTIV*", "brush": "AT"},
             "left": {"title": "Seinfeld"}, "first": "left"}
            """));

        Assert.Equal(
            [(2, "AT00"), (4, "R"), (17, "Bob's Burgers\x12Seinfeld"), (1, "1KTIV*\x12*")],
            packets.Select(Decode));
    }

    [Fact]
    public void PromoForTheLeftBoxOnly_HasAPatternBeforeTheSeparator()
    {
        var packets = ControlLineFeed.Promo(Json("""{"left": {"title": "Seinfeld", "brush": "DT"}}"""));

        Assert.Equal([(2, "00DT"), (17, "\x12Seinfeld"), (1, "1*\x12*")], packets.Select(Decode));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"title": ""}""")]
    [InlineData("""{"title": "A", "brush": "ABC"}""")]
    [InlineData("""{"title": "A", "right": {"title": "B"}}""")]
    [InlineData("""{"title": "A", "first": "top"}""")]
    [InlineData("""{"left": {"channels": "*"}}""")]
    public void Promo_RefusesABadRequest(string request)
    {
        Assert.Throws<FormatException>(() => ControlLineFeed.Promo(Json(request)));
    }

    [Fact]
    public void RawPackets_AreRead_WithTheSeparatorAsAnEscape()
    {
        var packets = ControlLineFeed.Packets(Json("""[{"type": 17, "body": "A\u0012B"}, {"type": 1, "body": "3"}]"""));

        Assert.Equal([(17, "A\u0012B"), (1, "3")], packets.Select(Decode));
    }

    [Fact]
    public void Line_SendsTheQueuedPackets_BeforeTheOtherSource_AndCountsTheBytes()
    {
        var feed = new ControlLineFeed(new ReplaySerialConnection([0xAA]));
        feed.Add([[0x01, 0x02], [0x03]]);
        Assert.Equal(3, feed.Queued);

        var bytes = new List<byte>();
        while (feed.TryRead(out var value))
            bytes.Add(value);

        Assert.Equal([0x01, 0x02, 0x03, 0xAA], bytes);
        Assert.Equal(4, feed.Sent);
        Assert.Equal(0, feed.Queued);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    /// <summary>The type and the body of a packet, after a check of its end and its checksum.</summary>
    private static (int, string) Decode(byte[] packet)
    {
        Assert.Equal(0x0D, packet[^2]);
        Assert.Equal(packet[..^1].Aggregate((byte)0, (sum, value) => (byte)(sum ^ value)), packet[^1]);
        return (packet[0], Encoding.Latin1.GetString(packet, 1, packet.Length - 3));
    }
}
