using AmigaSharp.Translator;

namespace AmigaSharp.Tests.Translator;

public class VasmListingTests
{
    [Fact]
    public void Read_HelloListing_FindsLinesLabelsAndSymbols()
    {
        var listing = VasmListing.Read(Path.Combine(TestPaths.RepositoryRoot, "samples", "HelloWorld", "hello.lst"));

        Assert.Equal(16, listing.Lines.Count);
        Assert.Equal(14, listing.Lines.Count(line => line.IsInstruction));
        var moveq = listing.Lines[1];
        Assert.Equal((0, 4u, "hello.s", 12, "MOVEQ"), (moveq.Section, moveq.Offset, moveq.File, moveq.LineNumber, moveq.Mnemonic));
        Assert.Equal(["NoDos"], listing.LabelsAt(0, 0x28));
        Assert.Equal(new ListingSymbol("OpenLibrary", null, 0xFFFFFDD8), listing.Symbols["OpenLibrary"]);
        Assert.Equal(new ListingSymbol("Hello", 0, 0x38), listing.Symbols["Hello"]);
    }

    [Fact]
    public void Parse_JoinsContinuationBytes_AndKeepsTheMacroCall()
    {
        string[] lines =
        [
            "Source: \"test.s\"",
            "                            \t     5: Name:   NStr \"dos.library\"",
            "00:0000000C 646F732E6C696272\t     1M     DC.B\t\"dos.library\",0",
            "00:00000014 617279",
            "00:00000017 00",
            "                            \t     2M     CNOP 0,2",
            "00:00000018 4E75            \t     6:     RTS ; return",
        ];

        var listing = VasmListing.Parse(lines);

        Assert.Equal(2, listing.Lines.Count);
        var data = listing.Lines[0];
        Assert.Equal(12, data.Bytes.Length);
        Assert.False(data.IsInstruction);
        Assert.Equal((5, "Name:   NStr \"dos.library\""), (data.LineNumber, data.MacroCall));
        Assert.Equal(["Name"], listing.LabelsAt(0, 0x0C));
        Assert.True(listing.Lines[1].IsInstruction);
    }

    [Theory]
    [InlineData("Start:  LEA DosName,A1 ; comment", "Start", "LEA")]
    [InlineData("Start   MOVEQ #1,D0", "Start", "MOVEQ")]
    [InlineData("    .loop: DBRA D0,.loop", ".loop", "DBRA")]
    [InlineData("    MOVE.L  #';',D0", null, "MOVE.L")]
    [InlineData("SysBase = 4", null, null)]
    [InlineData("Name=5", null, null)]
    [InlineData("Size    EQU 12", null, null)]
    [InlineData("Str macro", null, null)]
    [InlineData("; only a comment", null, null)]
    [InlineData("Label:", "Label", null)]
    public void SplitSource_FindsLabelAndMnemonic(string text, string? label, string? mnemonic)
    {
        Assert.Equal((label, mnemonic), VasmListing.SplitSource(text));
    }
}
