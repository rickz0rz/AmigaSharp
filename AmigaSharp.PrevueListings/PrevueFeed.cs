using System.Text;

namespace AmigaSharp.PrevueListings;

/// <summary>
/// Makes the serial data feed of Prevue: the commands that the satellite data feed sent to the Amiga at 2400 baud. ESQ
/// parses the commands, updates the grid at once, and saves the data to curday.dat and nxtday.dat.
/// </summary>
/// <remarks>
/// The formats come from the Prevue protocol document (prevueguide.com/Documentation/D2400.pdf) and from the parser of
/// ESQ (ESQPARS_ConsumeRbfByteAndDispatchCommand). Each command starts with $55 $AA and the command byte, and ends with
/// a NUL byte and a checksum. The checksum is $FF XOR the command byte XOR each byte of the data.
/// </remarks>
public static class PrevueFeed
{
    private const byte SourceStart = 0x12; // ^R
    private const byte ChannelNumber = 0x11; // ^Q
    private const byte SlotMask = 0x14; // ^T
    private const byte CallLetters = 0x01; // ^A

    /// <summary>The 'A' command: ESQ accepts the next commands if the selection code matches its own.</summary>
    /// <param name="selection">The type, the selection code and an extension, for example "A:GA24005" or "*".</param>
    public static byte[] Address(string selection) => Command((byte)'A', Encoding.ASCII.GetBytes(selection));

    /// <summary>
    /// The 'F' command: the configuration. It has the time zone '6', as the listing files have. ESQ reads exactly 20
    /// bytes and the NUL, so the reserved last field is a NUL byte.
    /// </summary>
    public static byte[] Configuration() => Command((byte)'F', [.. PrevueDataFile.ConfigurationFields, 0x00]);

    /// <summary>
    /// The 'K' command: the date and the time of the clock. ESQ applies it only when it takes its time from the feed.
    /// </summary>
    public static byte[] Clock(DateTime time) => Command((byte)'K',
    [
        (byte)time.DayOfWeek, (byte)(time.Month - 1), (byte)(time.Day - 1), (byte)(time.Year - 1900),
        (byte)time.Hour, (byte)time.Minute, (byte)time.Second, (byte)(time.IsDaylightSavingTime() ? 1 : 0),
    ]);

    /// <summary>
    /// The 'C' command: the channel lineup of a day. Each channel has a source name, a channel number, a mask with all
    /// 48 slots and its call letters. The 'P' commands find their channel by the source name.
    /// </summary>
    public static byte[] ChannelLineup(DateOnly date, IEnumerable<(string Source, PrevueChannel Channel)> channels) =>
        Command((byte)'C', ChannelLineupData(date, channels));

    /// <summary>The data of the 'C' command, without the header, the NUL and the checksum.</summary>
    public static byte[] ChannelLineupData(DateOnly date, IEnumerable<(string Source, PrevueChannel Channel)> channels)
    {
        var data = new List<byte> { PrevueDataFile.GroupCode(date) };
        foreach (var (source, channel) in channels)
        {
            data.Add(SourceStart);
            data.Add(0x01); // The source attribute: bit 0 is always 1.
            data.AddRange(Encoding.ASCII.GetBytes(source));
            data.Add(ChannelNumber);
            data.AddRange(Encoding.ASCII.GetBytes(channel.Number));
            data.Add(SlotMask);
            data.AddRange([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
            data.Add(CallLetters);
            data.AddRange(Encoding.ASCII.GetBytes(channel.CallLetters.Length > 6 ? channel.CallLetters[..6] : channel.CallLetters));
        }

        return data.ToArray();
    }

    /// <summary>The 'P' command: the text of a program that starts in a slot, on the channel with the source name.</summary>
    public static byte[] Program(DateOnly date, string source, PrevueProgram program)
    {
        var data = new List<byte> { (byte)program.Slot, PrevueDataFile.GroupCode(date) };
        data.AddRange(Encoding.ASCII.GetBytes(source));
        data.Add(SourceStart);
        data.Add((byte)(program.Movie ? 3 : 1));
        data.AddRange(Encoding.Latin1.GetBytes(program.Text));
        return Command((byte)'P', data.ToArray());
    }

    /// <summary>The '%' command: ESQ saves the text ads and the listings to its drive.</summary>
    public static byte[] Save() => Command((byte)'%', []);

    /// <summary>The box off command: ESQ does not accept more commands until the next 'A' command.</summary>
    public static byte[] BoxOff() => Command(0xBB, [0xBB]);

    /// <summary>
    /// Gives each channel a source name of a maximum of 6 characters. The names are different, because a 'P' command
    /// goes to each channel with its source name.
    /// </summary>
    public static List<(string Source, PrevueChannel Channel)> SourceNames(IEnumerable<PrevueChannel> channels)
    {
        var used = new HashSet<string>();
        var result = new List<(string, PrevueChannel)>();
        foreach (var channel in channels)
        {
            var name = new string(channel.CallLetters.Where(char.IsAsciiLetterOrDigit).Take(6).ToArray()).ToUpperInvariant();
            if (name.Length == 0)
                name = "CH";
            var candidate = name;
            for (var n = 2; !used.Add(candidate); n++)
                candidate = name[..Math.Min(name.Length, 6 - n.ToString().Length)] + n;
            result.Add((candidate, channel));
        }

        return result;
    }

    /// <summary>
    /// The feed of the listings of days: the address, the configuration, and for each day the lineup and the programs.
    /// At the end, ESQ saves the data and turns off.
    /// </summary>
    /// <remarks>
    /// The feed does not send the clock: ESQ applies a 'K' command only when it takes its time from the feed (a setting
    /// of '2'). Otherwise it counts the command as an error, and it keeps the time of its battery clock.
    /// </remarks>
    public static byte[] Listings(string selection, params PrevueDay[] days)
    {
        var feed = new MemoryStream();
        feed.Write(Address(selection));
        feed.Write(Configuration());
        foreach (var day in days)
        {
            var channels = SourceNames(day.Channels.Take(PrevueDataFile.MaximumChannels));
            feed.Write(ChannelLineup(day.Date, channels));
            foreach (var (source, channel) in channels)
            {
                foreach (var program in channel.Programs)
                    feed.Write(Program(day.Date, source, program));
            }
        }

        feed.Write(Save());
        feed.Write(BoxOff());
        return feed.ToArray();
    }

    /// <summary>
    /// The commands that bring ESQ from the listings of <paramref name="before"/> to the listings of
    /// <paramref name="after"/>. Returns an empty array if nothing changed.
    /// </summary>
    /// <remarks>
    /// If the lineup changed, or ESQ has no listings of the day, the feed sends the lineup and all the programs. ESQ
    /// then builds the day again. Otherwise it sends only the programs with a new or a changed text. A 'P' command
    /// cannot delete a program, so a program that is not in the guide any more stays in the grid.
    /// </remarks>
    public static byte[] Changes(string selection, IReadOnlyList<PrevueDay?> before, IReadOnlyList<PrevueDay> after)
    {
        var commands = new MemoryStream();
        for (var i = 0; i < after.Count; i++)
        {
            var day = after[i];
            var old = before.FirstOrDefault(d => d?.Date == day.Date);
            var channels = SourceNames(day.Channels.Take(PrevueDataFile.MaximumChannels));
            var oldChannels = old == null ? null : SourceNames(old.Channels.Take(PrevueDataFile.MaximumChannels));
            var lineup = ChannelLineupData(day.Date, channels);
            if (oldChannels == null || !lineup.SequenceEqual(ChannelLineupData(day.Date, oldChannels)))
            {
                commands.Write(Command((byte)'C', lineup));
                foreach (var (source, channel) in channels)
                {
                    foreach (var program in channel.Programs)
                        commands.Write(Program(day.Date, source, program));
                }

                continue;
            }

            for (var c = 0; c < channels.Count; c++)
            {
                var (source, channel) = channels[c];
                var oldPrograms = oldChannels[c].Channel.Programs.ToDictionary(p => p.Slot);
                foreach (var program in channel.Programs)
                {
                    if (!oldPrograms.TryGetValue(program.Slot, out var oldProgram) || oldProgram != program)
                        commands.Write(Program(day.Date, source, program));
                }
            }
        }

        if (commands.Length == 0)
            return [];
        return [.. Address(selection), .. Configuration(), .. commands.ToArray(), .. Save(), .. BoxOff()];
    }

    /// <summary>The checksum of a command: $FF XOR the command byte XOR each byte of the data.</summary>
    public static byte Checksum(byte command, IEnumerable<byte> data)
    {
        var checksum = (byte)(0xFF ^ command);
        foreach (var value in data)
            checksum ^= value;
        return checksum;
    }

    private static byte[] Command(byte command, byte[] data) => [0x55, 0xAA, command, .. data, 0x00, Checksum(command, data)];
}
