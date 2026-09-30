using System.Text;
using System.Text.Json;
using AmigaSharp.Host;
using AmigaSharp.PrevueLauncher;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Exec;

namespace AmigaSharp.Tests.Launcher;

public sealed class AutoPromoTests
{
    private readonly Core _core = new(new MemoryStream()) { Log = TextWriter.Null };
    private readonly EsqVariables _esq;

    public AutoPromoTests()
    {
        var block = _core.Allocator.Allocate(0x10000, MemoryFlags.Any | MemoryFlags.Clear);
        _esq = EsqVariables.FromBases([block, block])!;
        var memory = _core.Memory;
        memory.Write16(_esq[EsqVariables.CurrentSlot], 20);
        // Channel 4 KTIVDT: The Goldbergs in 21, a movie in 22, and a program in 23 that its mask does not have.
        AddChannel(0, "4", "KTIVDT", attribute: 0x01, [(21, "( 3:25) The Goldbergs |", 0x01),
            (22, "\"Casablanca\" (1942) A cafe owner meets an old love. (PG)", 0x03), (23, "Hidden Slot", 0x01)],
            slotsOff: [23]);
        // Channel 200 HBO, a premium channel: Seinfeld in 21.
        AddChannel(1, "200", "HBO", attribute: 0x03, [(21, "Seinfeld", 0x01)]);
        // Channel 5, hidden.
        AddChannel(2, "5", "HIDDEN", attribute: 0x09, [(21, "Nothing", 0x01)]);
        memory.Write16(_esq[EsqVariables.ChannelCount], 3);
    }

    [Theory]
    [InlineData("( 3:25) NFL Football |", false, "NFL Football", "NFL Football")]
    [InlineData("\"Casablanca\" (1942) A cafe owner. (PG)", true, "Casablanca", "\"Casablanca\"")]
    [InlineData("Seinfeld", false, "Seinfeld", "Seinfeld")]
    public void Titles_LeaveOutTheMarksOfTheGrid(string grid, bool movie, string title, string search)
    {
        Assert.Equal((title, search), PrevueGuide.Titles(grid, movie));
    }

    [Fact]
    public void Guide_HasTheProgramsOfTheVisibleChannels_InTheirSlots()
    {
        var programs = PrevueGuide.Read(_core.Memory, _esq);

        Assert.Equal(
            [("KTIVDT", 21, "The Goldbergs", false, false), ("KTIVDT", 22, "Casablanca", true, false),
                ("HBO", 21, "Seinfeld", false, true)],
            programs.Select(p => (p.CallLetters, p.Slot, p.Title, p.Movie, p.Premium)));
        Assert.Equal("4", programs[0].Number);
        Assert.Equal(60, programs[1].MinutesFrom(20));
    }

    [Theory]
    [InlineData("""{"movies": true}""", "Casablanca")]
    [InlineData("""{"premium": true}""", "Seinfeld")]
    [InlineData("""{"channels": ["HB*"]}""", "Seinfeld")]
    [InlineData("""{"channels": "4", "movies": false}""", "The Goldbergs")]
    [InlineData("""{"titles": ["casa"]}""", "Casablanca")]
    public void Pick_ChoosesAProgramThatFits(string criteria, string title)
    {
        var auto = new AutoPromo(_core.Memory, _esq);

        Assert.Equal(title, auto.Pick(Criteria(criteria))?.Title);
    }

    [Fact]
    public void Pick_DoesNotRepeatARecentTitle_UntilAllTitlesShowed()
    {
        var auto = new AutoPromo(_core.Memory, _esq);
        var criteria = Criteria("""{"within": 1}""");

        var titles = Enumerable.Range(0, 4).Select(_ => auto.Pick(criteria)!.Title).ToList();

        Assert.Equal(["The Goldbergs", "Seinfeld", "Casablanca", "The Goldbergs"], titles);
    }

    [Fact]
    public void Pick_WithNothingThatFits_IsNull()
    {
        Assert.Null(new AutoPromo(_core.Memory, _esq).Pick(Criteria("""{"titles": "Nothing"}""")));
    }

    [Fact]
    public void Packets_AreAPromoOfTheTitleOnItsChannel()
    {
        using var request = JsonDocument.Parse("""{"auto": {"movies": true}, "side": "left", "brush": "AT"}""");

        var packets = new AutoPromo(_core.Memory, _esq).Packets(request.RootElement, out var program);

        Assert.Equal("Casablanca", program.Title);
        Assert.Equal(
            [(2, "00AT"), (17, "\u0012\"Casablanca\""), (1, "1*\u0012KTIVDT")],
            packets.Select(packet => ((int)packet[0], Encoding.Latin1.GetString(packet, 1, packet.Length - 3))));
    }

    [Theory]
    [InlineData("""{"movies": "yes"}""")]
    [InlineData("""{"within": 0}""")]
    [InlineData("""{"order": "alphabetical"}""")]
    [InlineData("""{"channels": [4]}""")]
    public void BadCriteria_AreAnError(string criteria)
    {
        Assert.Throws<FormatException>(() => Criteria(criteria));
    }

    [Fact]
    public void ScheduleCue_WithAuto_IsChecked()
    {
        IScheduleExtension[] prevue =
            [new PrevueSchedule(new ControlLineRequests(new ControlLineFeed()), state: null, TextWriter.Null)];
        using var good = JsonDocument.Parse(
            """{"segments": [{"pause": 60, "top": [{"promo": {"auto": {"movies": true}}, "seconds": 30}]}]}""");
        using var bad = JsonDocument.Parse(
            """{"segments": [{"pause": 60, "top": [{"promo": {"auto": {"within": 99}}, "seconds": 30}]}]}""");

        Schedule.Parse(good.RootElement, null, prevue);
        Assert.Throws<FormatException>(() => Schedule.Parse(bad.RootElement, null, prevue));
    }

    private static AutoCriteria Criteria(string json)
    {
        using var document = JsonDocument.Parse(json);
        return AutoCriteria.Read(document.RootElement);
    }

    /// <summary>
    /// Adds a channel as ESQ has it: a record of 48 bytes, and a table with the call letters, the flag of each half
    /// hour at 7, and the pointer to the text of each half hour at 56.
    /// </summary>
    private void AddChannel(int index, string number, string callLetters, byte attribute,
        (int Slot, string Text, byte Flags)[] programs, int[]? slotsOff = null)
    {
        var memory = _core.Memory;
        var allocator = _core.Allocator;
        var record = allocator.Allocate(48, MemoryFlags.Any | MemoryFlags.Clear);
        WriteText(record + 1, number.PadLeft(3) + " ");
        WriteText(record + 19, callLetters);
        memory.Write8(record + 27, attribute);
        for (var slot = 1; slot <= 48; slot++)
        {
            if (slotsOff?.Contains(slot) != true)
                memory.Write8(record + 28 + (uint)((slot - 1) / 8),
                    (byte)(memory.Read8(record + 28 + (uint)((slot - 1) / 8)) | 1 << ((slot - 1) % 8)));
        }

        var table = allocator.Allocate(56 + 4 * 49, MemoryFlags.Any | MemoryFlags.Clear);
        WriteText(table, callLetters);
        foreach (var (slot, text, flags) in programs)
        {
            var address = allocator.Allocate((uint)text.Length + 1, MemoryFlags.Any | MemoryFlags.Clear);
            WriteText(address, text);
            memory.Write8(table + 7 + (uint)slot, flags);
            memory.Write32(table + 56 + 4 * (uint)slot, address);
        }

        memory.Write32(_esq[EsqVariables.ChannelRecords] + 4 * (uint)index, record);
        memory.Write32(_esq[EsqVariables.ChannelSlots] + 4 * (uint)index, table);
    }

    private void WriteText(uint address, string text)
    {
        for (var i = 0; i < text.Length; i++)
            _core.Memory.Write8(address + (uint)i, (byte)text[i]);
    }
}
