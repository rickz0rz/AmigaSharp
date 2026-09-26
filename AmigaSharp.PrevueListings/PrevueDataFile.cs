using System.Text;

namespace AmigaSharp.PrevueListings;

/// <summary>A program in the grid. It starts in a half-hour slot and continues until the next program.</summary>
/// <param name="Slot">The half-hour slot, 1 to 48. Slot 1 starts at 5:00 AM.</param>
/// <param name="Text">The text in the grid. A trailing " |" shows the closed-caption icon.</param>
/// <param name="Movie">True for a movie.</param>
public sealed record PrevueProgram(int Slot, string Text, bool Movie = false);

/// <param name="Number">The channel number, for example "4" or "56.2".</param>
/// <param name="CallLetters">The name of the channel. Prevue shows a maximum of 6 characters.</param>
public sealed record PrevueChannel(string Number, string CallLetters, IReadOnlyList<PrevueProgram> Programs);

/// <summary>The listings of one broadcast day. The day starts at 5:00 AM and ends at 4:59 AM of the next day.</summary>
public sealed record PrevueDay(DateOnly Date, IReadOnlyList<PrevueChannel> Channels);

/// <summary>
/// Writes the listing files of Prevue (ESQ): curday.dat for the current broadcast day and nxtday.dat for the next.
/// ESQ reads them at startup and at the change of the day, and it writes them when it saves the data that it received.
/// </summary>
/// <remarks>
/// The layout comes from DISKIO2_WriteCurDayDataFile and DISKIO2_LoadCurDayDataFile in the ESQ source. A number is
/// decimal ASCII text that ends with a NUL byte, and a string ends with a NUL byte.
/// <list type="bullet">
/// <item>curday.dat starts with the 21 bytes of the configuration (the 'F' command of the feed), the countdown, the
/// revision "DREV 5", a weather label and a status text. nxtday.dat does not have these fields.</item>
/// <item>Both files then have the group code (the day of the year, modulo 256), the number of channels, and the
/// checksum and the length of the last channel lineup command.</item>
/// <item>Each channel is a record of 48 bytes, the source name, and then each program: the slot, 4 flag numbers and
/// the text. The number 49 ends the channel. The 'P' commands of a feed find the channel by the source name.</item>
/// </list>
/// </remarks>
public static class PrevueDataFile
{
    /// <summary>ESQ does not save more than 200 channels.</summary>
    public const int MaximumChannels = 200;

    public const int SlotsPerDay = 48;

    private const int RecordSize = 48;
    private const int EndOfChannel = 49;

    /// <summary>
    /// The configuration of the 2020 data files, with the time zone '6'. ESQ adds (time zone - 6) hours to the times
    /// of the slots and of the clock. With '6', the display shows the times of the data and the clock without a
    /// change. The fields are those of the 'F' command in the Prevue protocol document.
    /// </summary>
    public static readonly byte[] ConfigurationFields = [.. "BE3366N"u8, 0x01, 0x01, .. "6YYNNNYANN"u8];

    /// <summary>The configuration as the files have it: the fields and two NUL bytes, 21 bytes.</summary>
    private static readonly byte[] Configuration = [.. ConfigurationFields, 0x00, 0x00];

    public static byte[] WriteCurrentDay(PrevueDay day)
    {
        var output = new MemoryStream();
        output.Write(Configuration);
        WriteNumber(output, 0);
        output.Write("DREV 5\0"u8);
        WriteString(output, "");
        WriteString(output, "");
        WriteChannels(output, day);
        return output.ToArray();
    }

    public static byte[] WriteNextDay(PrevueDay day)
    {
        var output = new MemoryStream();
        WriteChannels(output, day);
        return output.ToArray();
    }

    /// <summary>The group code of a day: its day of the year, modulo 256. ESQ uses only the data of the current day.</summary>
    public static byte GroupCode(DateOnly date) => (byte)date.DayOfYear;

    private static void WriteChannels(MemoryStream output, PrevueDay day)
    {
        var channels = day.Channels.Take(MaximumChannels).ToList();
        var code = GroupCode(day.Date);
        WriteNumber(output, code);
        WriteNumber(output, channels.Count);
        // The checksum and the length of the channel lineup command of the same channels, as ESQ saves them. ESQ
        // rebuilds the lineup only when a 'C' command has a different checksum or length.
        var sources = PrevueFeed.SourceNames(channels);
        var lineup = PrevueFeed.ChannelLineupData(day.Date, sources);
        WriteNumber(output, PrevueFeed.Checksum((byte)'C', lineup));
        WriteNumber(output, lineup.Length);

        // ESQ pads the channel numbers on the left to the width of the longest number, and a minimum of 3.
        var width = Math.Max(3, channels.Select(c => c.Number.Length).DefaultIfEmpty(0).Max());
        foreach (var (source, channel) in sources)
        {
            output.Write(Record(code, source, channel, width));
            WriteString(output, source);
            foreach (var program in channel.Programs.Where(p => p.Slot is >= 1 and <= SlotsPerDay).OrderBy(p => p.Slot))
            {
                WriteNumber(output, program.Slot);
                // The string attribute of the 'P' command: bit 0 is always 1, and bit 1 marks a movie.
                WriteNumber(output, program.Movie ? 3 : 1);
                WriteNumber(output, 0);
                WriteNumber(output, 0);
                WriteNumber(output, 0);
                WriteString(output, program.Text);
            }

            WriteNumber(output, EndOfChannel);
        }
    }

    /// <summary>
    /// The record of a channel. The known fields: the group code at 0, the channel number at 1 (right-aligned and a
    /// space), the source name at 12, the call letters at 19, the source attribute at 27, and the mask of the 48 slots
    /// at 28. The other bytes are the values that ESQ writes for a channel from a 'C' command. ESQ changes byte 40 while
    /// it runs.
    /// </summary>
    private static byte[] Record(byte code, string source, PrevueChannel channel, int width)
    {
        var record = new byte[RecordSize];
        record[0] = code;
        var number = channel.Number.PadLeft(width) + " ";
        Encoding.ASCII.GetBytes(number[..Math.Min(number.Length, 11)]).CopyTo(record, 1);
        Encoding.ASCII.GetBytes(source).CopyTo(record, 12);
        Encoding.ASCII.GetBytes(CallLetters(channel.CallLetters)).CopyTo(record, 19);
        record[27] = 0x01;
        for (var i = 28; i < 34; i++)
            record[i] = 0xFF;
        record[40] = 0x8A;
        record[41] = 0xFF;
        record[42] = 0xFF;
        record[43] = (byte)'0';
        record[44] = (byte)'0';
        record[47] = 0x03;
        return record;
    }

    private static string CallLetters(string text) => text.Length > 6 ? text[..6] : text;

    private static void WriteNumber(MemoryStream output, int value) => WriteString(output, value.ToString());

    private static void WriteString(MemoryStream output, string text)
    {
        output.Write(Encoding.Latin1.GetBytes(text));
        output.WriteByte(0);
    }
}
