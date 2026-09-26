using System.Text;
using AmigaSharp.PrevueListings;

namespace AmigaSharp.Tests.PrevueListings;

public class PrevueListingsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly DateTimeOffset DayStart = new(2026, 9, 25, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Convert_PutsEachProgramInTheSlotWhereItStarts()
    {
        var day = GuideConverter.Convert([Entry("4.1", Airing(0, 60, "News"), Airing(90, 30, "Talk"))], Day, Utc);

        var programs = day.Channels.Single().Programs;
        Assert.Equal([new PrevueProgram(1, "News"), new PrevueProgram(4, "Talk")], programs);
    }

    [Fact]
    public void Convert_AddsTheStartTime_TheCaptionIcon_AndTheMovieFlag()
    {
        var game = Airing(10 * 60 + 25, 180, "NFL Football") with { Tags = ["CC"] };
        var movie = Airing(14 * 60, 120, "Casablanca") with { Categories = ["Movie"] };

        var programs = GuideConverter.Convert([Entry("4.1", game, movie)], Day, Utc).Channels.Single().Programs;

        Assert.Equal(new PrevueProgram(21, "( 3:25) NFL Football |"), programs[0]);
        Assert.Equal(new PrevueProgram(29, "Casablanca", Movie: true), programs[1]);
    }

    [Fact]
    public void Convert_KeepsAProgramThatStartedBeforeTheDay_InSlotOne()
    {
        var programs = GuideConverter.Convert([Entry("4.1", Airing(-60, 90, "Early"))], Day, Utc).Channels.Single().Programs;

        Assert.Equal([new PrevueProgram(1, "Early")], programs);
    }

    [Fact]
    public void Convert_TakesTheHdChannels_InTheOrderOfTheirNumbers()
    {
        var entries = new[]
        {
            Entry("38.1"), Entry("2.10"), Entry("2.2"), Entry("5.1", hd: false), Entry("104.1"),
        };

        var numbers = GuideConverter.Convert(entries, Day, Utc).Channels.Select(c => c.Number);

        Assert.Equal(["2.2", "2.10", "38.1", "104.1"], numbers);
    }

    [Fact]
    public void Convert_MakesTheTextPrintable()
    {
        var programs = GuideConverter.Convert([Entry("4.1", Airing(0, 30, "Café | “Noir” – Déjà"))], Day, Utc)
            .Channels.Single().Programs;

        Assert.Equal("Cafe / \"Noir\" - Deja", programs.Single().Text);
    }

    [Theory]
    [InlineData("2026-09-27T05:29", "2026-09-26")]
    [InlineData("2026-09-27T05:30", "2026-09-27")]
    [InlineData("2026-09-27T00:10", "2026-09-26")]
    [InlineData("2026-09-26T23:59", "2026-09-26")]
    public void CurrentDay_ChangesAt530_AsEsqDoes(string time, string day)
    {
        Assert.Equal(DateOnly.Parse(day), GuideConverter.CurrentDay(DateTime.Parse(time)));
    }

    [Fact]
    public void WriteCurrentDay_HasTheLayoutThatEsqReads()
    {
        var channel = new PrevueChannel("4.1", "WDIVDT", [new PrevueProgram(21, "News |")]);
        var day = new PrevueDay(Day, [channel]);

        var bytes = PrevueDataFile.WriteCurrentDay(day);

        // The configuration (21 bytes, time zone '6'), the countdown, the revision, two empty strings, the group code
        // (day 268 modulo 256), 1 channel, and the checksum and the length of the channel lineup command.
        var lineup = PrevueFeed.ChannelLineupData(Day, [("WDIVDT", channel)]);
        var header = Encoding.Latin1.GetBytes("BE3366N\x01\x01" + "6YYNNNYANN\0\0" + "0\0DREV 5\0\0\0" + "12\0" + "1\0" +
                                              $"{PrevueFeed.Checksum((byte)'C', lineup)}\0{lineup.Length}\0");
        Assert.Equal(header, bytes[..header.Length]);
        var record = bytes.AsSpan(header.Length, 48);
        Assert.Equal(12, record[0]);
        Assert.Equal("4.1 ", Encoding.ASCII.GetString(record[1..5]));
        Assert.Equal("WDIVDT", Encoding.ASCII.GetString(record[12..18])); // The source name.
        Assert.Equal("WDIVDT", Encoding.ASCII.GetString(record[19..25])); // The call letters.
        Assert.Equal("WDIVDT\0" + "21\0" + "1\0" + "0\0" + "0\0" + "0\0" + "News |\0" + "49\0",
            Encoding.Latin1.GetString(bytes[(header.Length + 48)..]));
    }

    [Fact]
    public void WriteNextDay_HasNoConfiguration()
    {
        var bytes = PrevueDataFile.WriteNextDay(new PrevueDay(Day.AddDays(1), []));

        // The lineup of no channels is only the group code: its checksum is $FF XOR 'C' XOR 13, and its length is 1.
        Assert.Equal("13\0" + "0\0" + $"{0xFF ^ 'C' ^ 13}\0" + "1\0", Encoding.Latin1.GetString(bytes));
    }

    private static GuideEntry Entry(string number, params GuideAiring[] airings) => Entry(number, true, airings);

    private static GuideEntry Entry(string number, bool hd, params GuideAiring[] airings) =>
        new(new GuideChannel(number, "Name", "CALL", hd, Hidden: false), airings);

    private static GuideAiring Airing(int startMinutes, int minutes, string title) =>
        new(DayStart.AddMinutes(startMinutes).ToUnixTimeSeconds(), minutes * 60, title, null, null);
}
