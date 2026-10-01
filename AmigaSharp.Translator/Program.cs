using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

const string usage = """
    Usage: AmigaSharp.Translator <executable> --output <file.cs> [options]
           AmigaSharp.Translator <executable> --disassemble <file.s> [options]

    Translates an AmigaOS executable to a C# class, or writes the code that it finds as a disassembly.

    Options:
      --listing <file.lst>   The vasm listing of the executable (vasm option -L). The translator uses it to find
                             the instructions and the labels, and it copies the source lines into the comments.
      --namespace <name>     The namespace of the class. The default is AmigaSharp.Generated.
      --class <name>         The name of the class. The default is the name of the executable.
      --known-code <file>    A map of the code that ran (the launcher writes it, see --code-map of the launcher).
                             Without a listing, the translator also translates the code at its addresses.
      --disassemble <file>   Also write the code that the translator found as a disassembly: the functions, each
                             instruction with its address and its words, and the other bytes as data. With this
                             option, --output is optional.
    """;

string? executable = null, output = null, listingPath = null, knownCodePath = null, disassemblyPath = null;
var namespaceName = "AmigaSharp.Generated";
string? className = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output" when i + 1 < args.Length: output = args[++i]; break;
        case "--listing" when i + 1 < args.Length: listingPath = args[++i]; break;
        case "--namespace" when i + 1 < args.Length: namespaceName = args[++i]; break;
        case "--class" when i + 1 < args.Length: className = args[++i]; break;
        case "--known-code" when i + 1 < args.Length: knownCodePath = args[++i]; break;
        case "--disassemble" when i + 1 < args.Length: disassemblyPath = args[++i]; break;
        case var value when !value.StartsWith("--") && executable == null: executable = value; break;
        default:
            Console.Error.WriteLine($"error: unknown argument {args[i]}.");
            Console.Error.WriteLine(usage);
            return 2;
    }
}

if (executable == null || (output == null && disassemblyPath == null))
{
    Console.Error.WriteLine(usage);
    return 2;
}

var file = HunkFile.Read(executable);
var listing = listingPath == null ? null : VasmListing.Read(listingPath);
if (knownCodePath != null && !File.Exists(knownCodePath))
{
    Console.Error.WriteLine($"error: the map {knownCodePath} does not exist.");
    return 1;
}

var knownCode = knownCodePath == null ? null : KnownCodeFile.Read(knownCodePath);
if (knownCode != null && listing != null)
    Console.Error.WriteLine("warning: the listing tells which bytes are code, so the translator does not use --known-code.");
var analysis = ProgramAnalysis.Analyze(file, listing, knownCode);
foreach (var warning in analysis.Warnings)
    Console.Error.WriteLine($"warning: {warning}");

className ??= ProgramAnalysis.Sanitize(Path.GetFileNameWithoutExtension(executable));
var sourceName = Path.GetFileName(executable) + (listingPath == null ? "" : $" and {Path.GetFileName(listingPath)}");
if (output != null)
    File.WriteAllText(output, CSharpProgramWriter.Write(analysis, namespaceName, className, sourceName));
if (disassemblyPath != null)
    File.WriteAllText(disassemblyPath, Disassembler.Write(analysis, sourceName));

Console.WriteLine($"{Path.GetFileName(output ?? disassemblyPath)}: {analysis.Functions.Count} functions, "
                  + $"{analysis.Instructions.Count} instructions.");
Console.Write(HunkLayout.Describe(file, analysis.Bases));
return 0;
