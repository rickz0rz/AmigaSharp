using System.Text;
using System.Text.RegularExpressions;
using AmigaSharp.Runtime;

namespace AmigaSharp.PrevueLauncher;

/// <summary>A program of the listings of ESQ.</summary>
/// <param name="Channel">The index of the channel in the listings of ESQ, from 0.</param>
/// <param name="Number">The channel number, for example "4".</param>
/// <param name="Slot">The half hour of the broadcast day, from 1 to 48.</param>
/// <param name="Text">The text of the program in the grid.</param>
/// <param name="Title">The title of the program, without the time, the year and the marks of the grid.</param>
/// <param name="Search">The text that finds the program in a promo (type 17): a movie title is in quotation marks.</param>
public sealed record GuideProgram(
    int Channel, string Number, string CallLetters, bool Premium, int Slot, string Text, string Title, string Search,
    bool Movie)
{
    /// <summary>
    /// The minutes from the start of the current half hour to the start of the program's half hour. ESQ has its own
    /// rules for the time of the day (for example the change to daylight saving time), so the launcher does not give
    /// the time of the program.
    /// </summary>
    public int MinutesFrom(int currentSlot) => (Slot - currentSlot) * 30;
}

/// <summary>
/// Reads the listings of today from the memory of ESQ: the channels, and the programs of their half hours.
/// </summary>
/// <remarks>
/// For each channel, ESQ has a record of 48 bytes: the channel number at 1, the call letters at 19, the attribute at 27
/// (bit 1: premium, bit 3: hidden), and the mask of the half hours at 28. It also has a table of the half hours: the
/// call letters, a flag byte for each half hour at 7 (bit 1: movie), and a pointer to the text of each half hour at
/// 56. The half hours are 1 to 48, from the start of the broadcast day.
/// </remarks>
public static partial class PrevueGuide
{
    private const int Slots = 48;
    private const int RecordNumber = 1, RecordCallLetters = 19, RecordAttribute = 27, RecordSlotMask = 28;
    private const int TableFlags = 7, TableTexts = 56;
    private const byte PremiumBit = 0x02, HiddenBit = 0x08, MovieBit = 0x02, BlockedBit = 0x80;

    /// <summary>The current half hour of ESQ, from 1 to 48.</summary>
    public static int CurrentSlot(Memory memory, EsqVariables esq) => memory.Read16(esq[EsqVariables.CurrentSlot]);

    /// <summary>Reads the programs of today, in the order of the channels and of the half hours.</summary>
    public static List<GuideProgram> Read(Memory memory, EsqVariables esq)
    {
        var programs = new List<GuideProgram>();
        var count = memory.Read16(esq[EsqVariables.ChannelCount]);
        for (var channel = 0; channel < count && channel < 400; channel++)
        {
            var record = memory.Read32(esq[EsqVariables.ChannelRecords] + 4 * (uint)channel);
            var table = memory.Read32(esq[EsqVariables.ChannelSlots] + 4 * (uint)channel);
            if (record == 0 || table == 0)
                continue;
            var attribute = memory.Read8(record + RecordAttribute);
            if ((attribute & HiddenBit) != 0)
                continue;
            var number = ReadString(memory, record + RecordNumber, 11).Trim();
            var callLetters = ReadString(memory, record + RecordCallLetters, 8).Trim();
            for (var slot = 1; slot <= Slots; slot++)
            {
                var text = memory.Read32(table + TableTexts + 4 * (uint)slot);
                var flags = memory.Read8(table + TableFlags + (uint)slot);
                if (text == 0 || (flags & BlockedBit) != 0 || !SlotIsOn(memory, record, slot))
                    continue;
                var grid = ReadString(memory, text, 250);
                var movie = (flags & MovieBit) != 0;
                var (title, search) = Titles(grid, movie);
                if (title.Length == 0)
                    continue;
                programs.Add(new GuideProgram(channel, number, callLetters, (attribute & PremiumBit) != 0, slot, grid,
                    title, search, movie));
            }
        }

        return programs;
    }

    /// <summary>
    /// The title of a program, and the text that finds it. The text of the grid can start with the time, for example
    /// "( 3:25) NFL Football", and can end with "|" (closed captions). The text of a movie starts with the title in
    /// quotation marks: a promo finds it with the quotation marks, so it finds only that title.
    /// </summary>
    public static (string Title, string Search) Titles(string grid, bool movie)
    {
        var text = StartTime().Replace(grid, "");
        if (text.StartsWith('"') && text.IndexOf('"', 1) is var end and > 1)
        {
            var quoted = text[1..end];
            return (quoted, $"\"{quoted}\"");
        }

        var bar = text.IndexOf('|');
        if (bar >= 0)
            text = text[..bar];
        var title = new string(text.Where(c => c is >= ' ' and <= '~').ToArray()).Trim();
        return (title, title);
    }

    /// <summary>
    /// True if the mask of the channel has the half hour, as ESQ_TestBit1Based tests it: the lowest bit of byte 28 is
    /// the first half hour.
    /// </summary>
    private static bool SlotIsOn(Memory memory, uint record, int slot)
    {
        var bit = slot - 1;
        return (memory.Read8(record + RecordSlotMask + (uint)(bit / 8)) & (1 << (bit % 8))) != 0;
    }

    private static string ReadString(Memory memory, uint address, int maximum)
    {
        var text = new StringBuilder();
        for (var i = 0; i < maximum; i++)
        {
            var value = memory.Read8(address + (uint)i);
            if (value == 0)
                break;
            text.Append((char)value);
        }

        return text.ToString();
    }

    [GeneratedRegex(@"^\(\s*\d{1,2}:\d{2}\)\s*")]
    private static partial Regex StartTime();
}
