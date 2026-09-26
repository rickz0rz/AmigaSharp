using System.Globalization;
using System.Text.RegularExpressions;

namespace AmigaSharp.Translator;

/// <summary>One source line that produced bytes.</summary>
/// <param name="Section">The section number. For an executable, it is the hunk number.</param>
/// <param name="Offset">The offset of the first byte in the section.</param>
/// <param name="Source">The source text, without the line number.</param>
/// <param name="File">The source file of the line. For a macro expansion, it is the file of the macro call.</param>
/// <param name="LineNumber">The line number in <paramref name="File"/>. For a macro expansion, it is the line of the macro call.</param>
/// <param name="MacroCall">For a macro expansion, the source text of the macro call. Otherwise null.</param>
/// <param name="Mnemonic">The instruction or directive, in upper case, or null.</param>
public sealed record ListingLine(
    int Section,
    uint Offset,
    byte[] Bytes,
    string Source,
    string File,
    int LineNumber,
    string? MacroCall,
    string? Mnemonic)
{
    /// <summary>True if the line is a 68000 instruction and not data.</summary>
    public bool IsInstruction => Mnemonic != null && !VasmListing.IsDataDirective(Mnemonic);
}

/// <summary>A symbol from the symbol table of the listing.</summary>
/// <param name="Section">The section of a label, or null for an absolute value (an equate).</param>
public sealed record ListingSymbol(string Name, int? Section, uint Value);

/// <summary>
/// A listing file from vasm (option -L). It gives the source line, the section offset and the bytes of each
/// line, and the labels.
/// </summary>
public sealed partial class VasmListing
{
    private readonly Dictionary<(int Section, uint Offset), List<string>> _labels = new();

    public List<ListingLine> Lines { get; } = [];
    public Dictionary<string, ListingSymbol> Symbols { get; } = new();

    /// <summary>The labels at an address, in source order. Local labels start with a dot.</summary>
    public IReadOnlyList<string> LabelsAt(int section, uint offset) =>
        _labels.TryGetValue((section, offset), out var labels) ? labels : [];

    public static bool IsDataDirective(string mnemonic)
    {
        return mnemonic.StartsWith("DC", StringComparison.Ordinal)
               || mnemonic.StartsWith("DS", StringComparison.Ordinal)
               || mnemonic.StartsWith("BLK", StringComparison.Ordinal)
               || mnemonic is "CNOP" or "EVEN" or "ODD" or "ALIGN" or "INCBIN";
    }

    public static VasmListing Read(string path) => Parse(File.ReadLines(path));

    public static VasmListing Parse(IEnumerable<string> lines)
    {
        var listing = new VasmListing();
        var file = "";
        var pendingLabels = new List<string>();
        (string Text, int Number)? lastSourceLine = null;
        var inSymbols = false;

        foreach (var text in lines)
        {
            if (inSymbols)
            {
                var symbol = SymbolPattern().Match(text);
                if (!symbol.Success)
                {
                    // An external symbol has no value, for example "NAME external EXP". The table ends at the next
                    // table or at an empty line.
                    if (text.Length == 0 || text.StartsWith("Symbols by", StringComparison.Ordinal))
                        inSymbols = false;
                    continue;
                }

                var section = symbol.Groups["section"].Value;
                listing.Symbols[symbol.Groups["name"].Value] = new ListingSymbol(
                    symbol.Groups["name"].Value,
                    section == "E" ? null : int.Parse(section, NumberStyles.HexNumber),
                    uint.Parse(symbol.Groups["value"].Value, NumberStyles.HexNumber));
                continue;
            }

            if (text.StartsWith("Symbols by name:", StringComparison.Ordinal))
            {
                inSymbols = true;
                continue;
            }

            var source = SourcePattern().Match(text);
            if (source.Success)
            {
                file = source.Groups["file"].Value;
                continue;
            }

            var match = LinePattern().Match(text);
            if (!match.Success)
                continue;

            var bytes = match.Groups["bytes"].Success
                ? Convert.FromHexString(match.Groups["bytes"].Value)
                : [];

            // A line with bytes and no source text continues the bytes of the previous line.
            if (!match.Groups["source"].Success)
            {
                if (listing.Lines.Count > 0 && bytes.Length > 0)
                {
                    var last = listing.Lines[^1];
                    listing.Lines[^1] = last with { Bytes = [.. last.Bytes, .. bytes] };
                }

                continue;
            }

            var sourceText = match.Groups["source"].Value;
            var number = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
            var isMacroExpansion = match.Groups["kind"].Value == "M";
            if (!isMacroExpansion)
                lastSourceLine = (sourceText, number);

            var (label, mnemonic) = SplitSource(sourceText);
            if (label != null)
                pendingLabels.Add(label);

            if (bytes.Length == 0)
                continue;

            var sectionNumber = int.Parse(match.Groups["section"].Value, NumberStyles.HexNumber);
            var offset = uint.Parse(match.Groups["offset"].Value, NumberStyles.HexNumber);
            if (pendingLabels.Count > 0)
            {
                if (!listing._labels.TryGetValue((sectionNumber, offset), out var labels))
                    listing._labels[(sectionNumber, offset)] = labels = [];
                labels.AddRange(pendingLabels);
                pendingLabels.Clear();
            }

            listing.Lines.Add(new ListingLine(
                sectionNumber,
                offset,
                bytes,
                sourceText,
                file,
                isMacroExpansion ? lastSourceLine?.Number ?? number : number,
                isMacroExpansion ? lastSourceLine?.Text : null,
                mnemonic));
        }

        return listing;
    }

    /// <summary>
    /// Finds the label and the mnemonic of a source line. A label starts in column 0 or ends with a colon.
    /// An assignment such as "Name = 5" or "Name EQU 5" is not a label.
    /// </summary>
    public static (string? Label, string? Mnemonic) SplitSource(string text)
    {
        var code = StripComment(text);
        if (code.Length == 0 || code[0] == '*')
            return (null, null);

        var tokens = code.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return (null, null);

        string? label = null;
        var index = 0;
        if (!char.IsWhiteSpace(code[0]) || tokens[0].EndsWith(':'))
        {
            label = tokens[0].TrimEnd(':');
            index = 1;
            // "Name=5" has no space around the equals sign.
            if (label.Contains('='))
                return (null, null);
        }

        var mnemonic = index < tokens.Length ? tokens[index].ToUpperInvariant() : null;
        if (mnemonic != null && (mnemonic.StartsWith('=') || mnemonic is "EQU" or "SET" or "MACRO" or "EQUR" or "REG"))
            return (null, null);

        return (label, mnemonic);
    }

    private static string StripComment(string text)
    {
        var inQuote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuote != '\0')
            {
                if (c == inQuote)
                    inQuote = '\0';
            }
            else if (c is '"' or '\'')
            {
                inQuote = c;
            }
            else if (c == ';')
            {
                return text[..i].TrimEnd();
            }
        }

        return text.TrimEnd();
    }

    [GeneratedRegex("""^Source: "(?<file>.*)"$""")]
    private static partial Regex SourcePattern();

    // "00:00000004 7024        	     8: text", "                            	     8: text",
    // "00:0000000C 646F732E	     1M text" (a macro expansion) or "00:00000014 617279" (more bytes).
    [GeneratedRegex(@"^(?:(?<section>[0-9A-F]{2}):(?<offset>[0-9A-F]{8}) (?<bytes>[0-9A-F]+))?\s*(?:\t\s*(?<line>\d+)(?<kind>[:M]) ?(?<source>.*))?$")]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"^(?<name>\S+)\s+(?<section>[0-9A-F]{2}|E):(?<value>[0-9A-F]{8})(?:\s+\S+)*$")]
    private static partial Regex SymbolPattern();
}
