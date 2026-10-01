using System.Globalization;
using System.Text;
using AmigaSharp.Runtime.Loader;

namespace AmigaSharp.Translator;

/// <summary>
/// Writes the code that an analysis found as a disassembly, for inspection: each hunk, the functions as labels, each
/// instruction with its address and its words, and the other bytes as data.
/// </summary>
/// <remarks>
/// The disassembly shows what the translator translates. Without a listing, that is the code from the entry point,
/// from the calls and the relocated pointers, and from the map of the code that ran (see <see cref="KnownCodeFile"/>).
/// Code that the analysis did not find shows as data. The addresses are the load addresses of the runtime, and the
/// instructions use the syntax of the decoder, so the text is not a source that an assembler reads.
/// </remarks>
public static class Disassembler
{
    private const int DataBytesPerLine = 16;

    public static string Write(ProgramAnalysis analysis, string sourceName)
    {
        var text = new StringBuilder();
        var instructions = analysis.Instructions.Count;
        text.AppendLine($"; Disassembly of {sourceName}: {analysis.Functions.Count} functions, {instructions} instructions.");
        text.AppendLine("; The code that the translator found. The other bytes of the hunks show as data.");
        text.AppendLine("; The words are the bytes of the file. The operands show the addresses after the relocation.");
        foreach (var hunk in analysis.File.Hunks)
        {
            var start = analysis.Bases[hunk.Index];
            text.AppendLine();
            text.AppendLine($"; hunk {hunk.Index}: {hunk.Type} {hunk.Memory}, ${start:X6}, {hunk.Size} bytes");
            var address = start;
            var dataEnd = start + (uint)hunk.Data.Length;
            while (address < dataEnd)
            {
                if (analysis.FunctionAt(address) is { } function)
                {
                    text.AppendLine();
                    text.AppendLine($"{function.Name}:");
                }

                if (analysis.Instructions.TryGetValue(address, out var code))
                {
                    var instruction = code.Instruction;
                    var words = new StringBuilder();
                    for (var offset = 0; offset < instruction.Length; offset += 2)
                    {
                        var index = (int)(address - start) + offset;
                        words.Append(CultureInfo.InvariantCulture, $"{hunk.Data[index]:X2}{hunk.Data[index + 1]:X2} ");
                    }

                    text.AppendLine($"    ${address:X6}  {words,-25}{instruction}");
                    address = instruction.NextAddress;
                    continue;
                }

                // Data up to the next instruction or function, the end of the line of data, or the end of the hunk.
                var end = Math.Min(dataEnd, address + DataBytesPerLine);
                var next = address + 1;
                while (next < end && !analysis.Instructions.ContainsKey(next))
                    next++;
                var bytes = string.Join(",", Enumerable.Range((int)(address - start), (int)(next - address))
                    .Select(index => $"${hunk.Data[index]:X2}"));
                text.AppendLine($"    ${address:X6}  dc.b {bytes}");
                address = next;
            }

            if (hunk.Size > hunk.Data.Length)
                text.AppendLine($"    ${dataEnd:X6}  ds.b {hunk.Size - (uint)hunk.Data.Length}");
        }

        return text.ToString();
    }
}
