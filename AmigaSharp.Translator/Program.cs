using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

const string usage = """
    Usage: AmigaSharp.Translator <executable> --output <file.cs> [options]

    Translates an AmigaOS executable to a C# class.

    Options:
      --listing <file.lst>   The vasm listing of the executable (vasm option -L). The translator uses it to find
                             the instructions and the labels, and it copies the source lines into the comments.
      --namespace <name>     The namespace of the class. The default is AmigaSharp.Generated.
      --class <name>         The name of the class. The default is the name of the executable.
    """;

string? executable = null, output = null, listingPath = null;
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
        case var value when !value.StartsWith("--") && executable == null: executable = value; break;
        default:
            Console.Error.WriteLine($"error: unknown argument {args[i]}.");
            Console.Error.WriteLine(usage);
            return 2;
    }
}

if (executable == null || output == null)
{
    Console.Error.WriteLine(usage);
    return 2;
}

var file = HunkFile.Read(executable);
var listing = listingPath == null ? null : VasmListing.Read(listingPath);
var analysis = ProgramAnalysis.Analyze(file, listing);
foreach (var warning in analysis.Warnings)
    Console.Error.WriteLine($"warning: {warning}");

className ??= ProgramAnalysis.Sanitize(Path.GetFileNameWithoutExtension(executable));
var sourceName = Path.GetFileName(executable) + (listingPath == null ? "" : $" and {Path.GetFileName(listingPath)}");
File.WriteAllText(output, CSharpProgramWriter.Write(analysis, namespaceName, className, sourceName));

Console.WriteLine($"{Path.GetFileName(output)}: {analysis.Functions.Count} functions, "
                  + $"{analysis.Instructions.Count} instructions.");
Console.Write(HunkLayout.Describe(file, analysis.Bases));
return 0;
