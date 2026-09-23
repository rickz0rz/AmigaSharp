using System.Globalization;
using System.Text.RegularExpressions;
using AmigaSharp.Runtime.Cpu;

namespace AmigaSharp.Tests.Cpu;

/// <summary>
/// Decodes every instruction in the vasm listing of the target program. The decoded instructions of each line must
/// use exactly the bytes that vasm emitted for it. Run scripts/build-target.sh to make build/target/ESQ.lst.
/// </summary>
public partial class TargetListingTests
{
    private static readonly HashSet<string> DataDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "DC", "DC.B", "DC.W", "DC.L", "DCB", "DCB.B", "DCB.W", "DCB.L", "DS", "DS.B", "DS.W", "DS.L",
        "CNOP", "EVEN", "INCBIN", "ALIGN", "ALIGN_WORD", "STR", "NSTR", "NSTR2", "NSTR3",
    };

    [Fact]
    public void EveryListedInstruction_DecodesToTheAssembledLength()
    {
        var path = Path.Combine(TestPaths.RepositoryRoot, "build", "target", "ESQ.lst");
        if (!File.Exists(path))
            Assert.Skip("The target listing is not built. Run scripts/build-target.sh.");

        var lines = ParseListing(path);
        var failures = new List<string>();
        var decoded = 0;
        foreach (var line in lines)
        {
            if (line.Section != 0 || line.Bytes.Length == 0 || !IsInstruction(line.Source))
                continue;

            // The vasm optimizer can emit more than one instruction for a source line. For example, it changes
            // MOVEM.L (A7)+,A2-A3 to two MOVEA.L instructions. Decode until all the bytes of the line are used.
            var bytes = line.Bytes;
            var offset = 0u;
            while (offset < bytes.Length)
            {
                var instruction = Decoder.Decode(offset, address =>
                    address + 1 < bytes.Length ? (ushort)(bytes[address] << 8 | bytes[address + 1]) : (ushort)0);
                decoded++;
                if (instruction.Operation is Operation.Illegal or Operation.LineA or Operation.LineF)
                {
                    failures.Add($"${line.Offset + offset:X6}: illegal: {line.Source.Trim()}");
                    break;
                }

                offset += (uint)instruction.Length;
            }

            if (offset > bytes.Length)
                failures.Add($"${line.Offset:X6}: {bytes.Length} bytes, decoded {offset}: {line.Source.Trim()}");
        }

        Assert.True(decoded > 10_000, $"Only {decoded} instructions were found in the listing.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {decoded} differ:\n" + string.Join("\n", failures.Take(20)));
    }

    private static bool IsInstruction(string source)
    {
        var text = source;
        var comment = text.IndexOf(';');
        if (comment >= 0)
            text = text[..comment];

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        // A label starts in column 0 or ends with a colon.
        if (tokens.Count > 0 && (!char.IsWhiteSpace(source.FirstOrDefault()) || tokens[0].EndsWith(':')))
            tokens.RemoveAt(0);
        return tokens.Count > 0 && !DataDirectives.Contains(tokens[0]) && !tokens[0].Contains('=');
    }

    private sealed record ListingLine(int Section, uint Offset, byte[] Bytes, string Source);

    /// <summary>
    /// Reads lines of the form "00:00000004 7024  	     8:     MOVEQ #36,D0". A line from a macro expansion has
    /// "M" in place of the colon. A long byte sequence continues on the next lines, which have an address and bytes
    /// but no source text.
    /// </summary>
    private static List<ListingLine> ParseListing(string path)
    {
        var result = new List<ListingLine>();
        foreach (var text in File.ReadLines(path))
        {
            var match = ListingLinePattern().Match(text);
            if (!match.Success)
                continue;

            var bytes = Convert.FromHexString(match.Groups["bytes"].Value);
            if (!match.Groups["source"].Success)
            {
                if (result.Count > 0)
                {
                    var last = result[^1];
                    result[^1] = last with { Bytes = [.. last.Bytes, .. bytes] };
                }

                continue;
            }

            result.Add(new ListingLine(
                int.Parse(match.Groups["section"].Value, NumberStyles.HexNumber),
                uint.Parse(match.Groups["offset"].Value, NumberStyles.HexNumber),
                bytes,
                match.Groups["source"].Value));
        }

        return result;
    }

    [GeneratedRegex(@"^(?<section>[0-9A-F]{2}):(?<offset>[0-9A-F]{8}) (?<bytes>[0-9A-F]+)\s*(\t\s*\d+(:|M) (?<source>.*))?$")]
    private static partial Regex ListingLinePattern();
}
