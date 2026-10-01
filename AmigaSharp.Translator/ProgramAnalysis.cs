using System.Text;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Cpu;
using AmigaSharp.Runtime.Loader;
using Decoder = AmigaSharp.Runtime.Cpu.Decoder;

namespace AmigaSharp.Translator;

/// <summary>One decoded instruction and the source line that produced it.</summary>
/// <param name="Line">The listing line, or null if the translator has no listing.</param>
/// <param name="FirstOfLine">
/// True if this is the first instruction of the line. The vasm optimizer can emit more than one instruction for a line.
/// </param>
public sealed record CodeInstruction(Instruction Instruction, ListingLine? Line, bool FirstOfLine);

/// <summary>A range of code that becomes one C# method.</summary>
public sealed class Function
{
    public required uint Start { get; init; }
    public required string Name { get; init; }
    public List<CodeInstruction> Instructions { get; } = [];

    /// <summary>The address after the last byte of the function.</summary>
    public uint End { get; set; }

    public bool Contains(uint address) => address >= Start && address < End;
}

/// <summary>
/// Finds the instructions and the functions of a program. The listing tells which bytes are instructions.
/// Without a listing, the analysis follows the code from the entry point.
/// </summary>
public sealed class ProgramAnalysis
{
    public required HunkFile File { get; init; }
    public required uint[] Bases { get; init; }
    public required VasmListing? Listing { get; init; }

    /// <summary>All instructions, by address.</summary>
    public SortedDictionary<uint, CodeInstruction> Instructions { get; } = new();

    /// <summary>All functions, by start address.</summary>
    public SortedDictionary<uint, Function> Functions { get; } = new();

    public List<string> Warnings { get; } = [];

    /// <param name="knownCode">
    /// Addresses where code ran in earlier runs, for example the entries of the interpreter. Without a listing, the
    /// analysis also follows the code from each of them, also in a data hunk, and each of them starts a function. With a
    /// listing, the listing tells which bytes are code, and the analysis does not use them.
    /// </param>
    public static ProgramAnalysis Analyze(HunkFile file, VasmListing? listing, IReadOnlyCollection<uint>? knownCode = null)
    {
        var bases = HunkLayout.Assign(file);
        var memory = new Memory(guardHardware: false);
        file.Load(memory, bases);

        var analysis = new ProgramAnalysis { File = file, Bases = bases, Listing = listing };
        var known = listing == null ? (knownCode ?? []).Where(analysis.IsInHunkData).ToList() : [];
        if (listing != null)
            analysis.FindInstructionsFromListing(memory, listing);
        else
            analysis.FindInstructionsFromEntry(memory, known);

        analysis.FindFunctions(memory, known);
        return analysis;
    }

    public bool IsInstruction(uint address) => Instructions.ContainsKey(address);

    public Function? FunctionAt(uint address) => Functions.GetValueOrDefault(address);

    /// <summary>The address in the program of an offset in a hunk.</summary>
    public uint AddressOf(int hunk, uint offset) => Bases[hunk] + offset;

    private void FindInstructionsFromListing(Memory memory, VasmListing listing)
    {
        foreach (var line in listing.Lines)
        {
            if (!line.IsInstruction || line.Section >= File.Hunks.Count || File.Hunks[line.Section].Type != HunkType.Code)
                continue;

            var address = AddressOf(line.Section, line.Offset);
            var end = address + (uint)line.Bytes.Length;
            var first = true;
            while (address < end)
            {
                var instruction = Decoder.Decode(address, memory.Read16);
                Instructions[address] = new CodeInstruction(instruction, line, first);
                first = false;
                address = instruction.NextAddress;
            }

            if (address != end)
                Warnings.Add($"${end:X6}: the instructions of \"{line.Source.Trim()}\" do not end at the end of the line.");
        }
    }

    private void FindInstructionsFromEntry(Memory memory, IReadOnlyList<uint> knownCode)
    {
        // Code that ran can be in a data hunk. The analysis then follows code in that hunk too.
        var codeHunks = File.Hunks.Where(hunk => hunk.Type == HunkType.Code).Select(hunk => hunk.Index)
            .Concat(knownCode.Select(address => HunkOffset(address).Hunk)).ToHashSet();
        var pending = new Stack<uint>(knownCode.Reverse());
        pending.Push(Bases[0]);
        while (pending.Count > 0)
        {
            var address = pending.Pop();
            while (!Instructions.ContainsKey(address) && (address & 1) == 0
                   && HunkOffset(address).Hunk is var hunk and >= 0 && codeHunks.Contains(hunk))
            {
                Instruction instruction;
                try
                {
                    instruction = Decoder.Decode(address, memory.Read16);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // The path runs into bytes that are not an instruction, for example data after the code.
                    break;
                }

                Instructions[address] = new CodeInstruction(instruction, null, true);
                foreach (var target in ConstantTargets(instruction))
                    pending.Push(target);
                if (InstructionEmitter.EndsFlow(instruction))
                    break;
                address = instruction.NextAddress;
            }
        }
    }

    private void FindFunctions(Memory memory, IReadOnlyList<uint> knownCode)
    {
        var starts = new SortedSet<uint> { Bases[0] };

        // Every address where code ran in an earlier run: the program called it or jumped to it.
        starts.UnionWith(knownCode);

        // Every non-local label in a code hunk that is at an instruction.
        if (Listing != null)
        {
            foreach (var symbol in Listing.Symbols.Values)
            {
                if (symbol.Section is { } section && section < File.Hunks.Count
                    && IsInstruction(AddressOf(section, symbol.Value)))
                    starts.Add(AddressOf(section, symbol.Value));
            }
        }

        // Every target of BSR and JSR.
        foreach (var code in Instructions.Values)
        {
            var i = code.Instruction;
            if (i.Operation == Operation.Bsr)
                starts.Add(i.Target);
            else if (i.Operation == Operation.Jsr && InstructionEmitter.ConstantAddress(i.Source) is { } target)
                starts.Add(target);
        }

        // Every relocated pointer to an instruction. The pointer can be a function pointer or an entry in a jump table.
        foreach (var hunk in File.Hunks)
        {
            foreach (var relocation in hunk.Relocations)
                starts.Add(memory.Read32(AddressOf(hunk.Index, relocation.Offset)));
        }

        starts.RemoveWhere(address => !IsInstruction(address));

        var names = new HashSet<string>(ReservedNames, StringComparer.Ordinal);
        var ordered = starts.ToList();
        for (var n = 0; n < ordered.Count; n++)
        {
            var start = ordered[n];
            var hunkEnd = HunkEnd(start);
            var end = n + 1 < ordered.Count && ordered[n + 1] < hunkEnd ? ordered[n + 1] : hunkEnd;
            Functions[start] = new Function { Start = start, Name = UniqueName(FunctionName(start), start, names), End = end };
        }

        // Both lists are in address order, so one pass puts each instruction in its function.
        using var functions = Functions.Values.GetEnumerator();
        var current = functions.MoveNext() ? functions.Current : null;
        foreach (var code in Instructions.Values)
        {
            var address = code.Instruction.Address;
            while (current != null && address >= current.End)
                current = functions.MoveNext() ? functions.Current : null;
            if (current != null && current.Contains(address))
                current.Instructions.Add(code);
        }
    }

    /// <summary>The names that a function cannot have: C# keywords and the members of the base class.</summary>
    private static readonly string[] ReservedNames =
    [
        "Run", "RegisterFunctions", "core", "cpu", "memory", "Equals", "GetHashCode", "GetType", "ToString",
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    ];

    /// <summary>The constant targets of a branch, a call or a jump.</summary>
    public static IEnumerable<uint> ConstantTargets(Instruction i)
    {
        switch (i.Operation)
        {
            case Operation.Bcc or Operation.Bsr or Operation.Dbcc:
                yield return i.Target;
                break;
            case Operation.Jmp or Operation.Jsr:
                if (InstructionEmitter.ConstantAddress(i.Source) is { } target)
                    yield return target;
                break;
        }
    }

    private string FunctionName(uint address)
    {
        var (hunk, offset) = HunkOffset(address);
        var labels = Listing?.LabelsAt(hunk, offset) ?? [];
        var label = labels.FirstOrDefault(name => !name.StartsWith('.')) ?? labels.FirstOrDefault();
        return label == null ? $"Sub_{address:X6}" : Sanitize(label);
    }

    private static string UniqueName(string name, uint address, HashSet<string> names)
    {
        if (!names.Add(name))
        {
            name = $"{name}_{address:X6}";
            names.Add(name);
        }

        return name;
    }

    /// <summary>Makes a C# identifier from a label.</summary>
    public static string Sanitize(string label)
    {
        var text = new StringBuilder();
        foreach (var c in label.TrimStart('.'))
            text.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (text.Length == 0 || char.IsDigit(text[0]))
            text.Insert(0, '_');
        return text.ToString();
    }

    public (int Hunk, uint Offset) HunkOffset(uint address)
    {
        for (var hunk = 0; hunk < Bases.Length; hunk++)
        {
            if (address >= Bases[hunk] && address < Bases[hunk] + File.Hunks[hunk].Size)
                return (hunk, address - Bases[hunk]);
        }

        return (-1, 0);
    }

    /// <summary>True if the address is even and in the contents of a hunk in the file (not in BSS memory).</summary>
    public bool IsInHunkData(uint address)
    {
        var (hunk, offset) = HunkOffset(address);
        return (address & 1) == 0 && hunk >= 0 && offset + 2 <= File.Hunks[hunk].Data.Length;
    }

    private uint HunkEnd(uint address)
    {
        var (hunk, _) = HunkOffset(address);
        return Bases[hunk] + File.Hunks[hunk].Size;
    }
}
