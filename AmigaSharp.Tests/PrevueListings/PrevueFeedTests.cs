using System.Text;
using AmigaSharp.PrevueListings;

namespace AmigaSharp.Tests.PrevueListings;

public class PrevueFeedTests
{
    private static readonly DateOnly Day = new(2026, 9, 25); // Day 268: group code 12.

    [Fact]
    public void Program_HasTheLayoutOfThePCommand_AndTheChecksum()
    {
        var command = PrevueFeed.Program(Day, "WDIVDT", new PrevueProgram(21, "News", Movie: true));

        byte[] data = [21, 12, .. "WDIVDT"u8, 0x12, 3, .. "News"u8];
        var checksum = data.Aggregate((byte)(0xFF ^ 'P'), (sum, value) => (byte)(sum ^ value));
        Assert.Equal([0x55, 0xAA, (byte)'P', .. data, 0x00, checksum], command);
    }

    [Fact]
    public void Configuration_HasTwentyDataBytes_AsEsqReadsThem()
    {
        var command = PrevueFeed.Configuration();

        // $55 $AA 'F', 20 bytes, the NUL and the checksum.
        Assert.Equal(3 + 20 + 1 + 1, command.Length);
        Assert.Equal((byte)'6', command[3 + 9]); // The time zone.
    }

    [Fact]
    public void ChannelLineup_HasTheSourceTheNumberTheMaskAndTheCallLetters()
    {
        var channel = new PrevueChannel("4.1", "WDIVDT", []);

        var data = PrevueFeed.ChannelLineupData(Day, [("WDIVDT", channel)]);

        Assert.Equal([12, 0x12, 0x01, .. "WDIVDT"u8, 0x11, .. "4.1"u8, 0x14, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, .. "WDIVDT"u8], data);
    }

    [Fact]
    public void ChannelLineup_SetsBit1OfTheSourceAttribute_ForAPremiumChannel()
    {
        var channel = new PrevueChannel("222", "AMCHD", [], Premium: true);

        var data = PrevueFeed.ChannelLineupData(Day, [("AMCHD", channel)]);

        Assert.Equal(0x03, data[2]);
    }

    [Fact]
    public void SourceNames_AreDifferent_ForTheSameCallLetters()
    {
        var channels = new[] { Channel("4.1", "WDIVDT"), Channel("104.1", "WDIVDT"), Channel("7.1", "") };

        var names = PrevueFeed.SourceNames(channels).Select(pair => pair.Source);

        Assert.Equal(["WDIVDT", "WDIVD2", "CH"], names);
    }

    [Fact]
    public void Changes_SendsOnlyTheChangedPrograms()
    {
        var before = new PrevueDay(Day, [Channel("4.1", "WDIVDT", new PrevueProgram(1, "News"), new PrevueProgram(2, "Talk"))]);
        var after = new PrevueDay(Day, [Channel("4.1", "WDIVDT", new PrevueProgram(1, "News"), new PrevueProgram(2, "Game"))]);

        var feed = PrevueFeed.Changes("*", [before], [after]);

        Assert.Equal(1, Count(feed, 'P'));
        Assert.Equal(0, Count(feed, 'C'));
        Assert.Contains("Game", Encoding.Latin1.GetString(feed));
        Assert.Equal(1, Count(feed, '%'));
    }

    [Fact]
    public void Changes_SendsTheWholeDay_ForANewDayOrANewLineup()
    {
        var day = new PrevueDay(Day, [Channel("4.1", "WDIVDT", new PrevueProgram(1, "News"), new PrevueProgram(2, "Talk"))]);
        var newLineup = day with { Channels = [.. day.Channels, Channel("7.1", "WXYZDT")] };

        Assert.Equal((1, 2), (Count(PrevueFeed.Changes("*", [null], [day]), 'C'), Count(PrevueFeed.Changes("*", [null], [day]), 'P')));
        Assert.Equal((1, 2), (Count(PrevueFeed.Changes("*", [day], [newLineup]), 'C'), Count(PrevueFeed.Changes("*", [day], [newLineup]), 'P')));
    }

    [Fact]
    public void Changes_IsEmpty_WhenNothingChanged()
    {
        var day = new PrevueDay(Day, [Channel("4.1", "WDIVDT", new PrevueProgram(1, "News"))]);

        Assert.Empty(PrevueFeed.Changes("*", [day], [day with { Channels = [Channel("4.1", "WDIVDT", new PrevueProgram(1, "News"))] }]));
    }

    private static PrevueChannel Channel(string number, string callLetters, params PrevueProgram[] programs) =>
        new(number, callLetters, programs);

    /// <summary>The number of commands of a type in a feed: $55 $AA and the command byte.</summary>
    private static int Count(byte[] feed, char command)
    {
        var count = 0;
        for (var i = 0; i + 2 < feed.Length; i++)
        {
            if (feed[i] == 0x55 && feed[i + 1] == 0xAA && feed[i + 2] == command)
                count++;
        }

        return count;
    }
}
