using System.Reflection;
using System.Security.Cryptography;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AmigaSharp.Host;

/// <summary>
/// Translates an executable and compiles the C# code in memory. The compiled assembly goes to a cache, with the hash
/// of the executable, the listing and the translator as its name. So the next start does not translate again.
/// </summary>
public static class ProgramCompiler
{
    private const string Namespace = "AmigaSharp.Launched";
    private const string ClassName = "TranslatedMain";

    public static string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "AmigaSharp", "translations");

    /// <summary>Returns the type of the translated program. It is a subclass of <see cref="TranslatedProgram"/>.</summary>
    public static Type Compile(byte[] executable, string? listingPath, TextWriter log)
    {
        var listingBytes = listingPath == null ? [] : File.ReadAllBytes(listingPath);
        var key = CacheKey(executable, listingBytes);
        var cached = Path.Combine(CacheDirectory, key + ".dll");
        if (File.Exists(cached))
        {
            log.WriteLine($"Using the translation in {cached}.");
            return Assembly.LoadFile(cached).GetType($"{Namespace}.{ClassName}")!;
        }

        log.WriteLine("Translating the executable.");
        var listing = listingPath == null ? null : VasmListing.Read(listingPath);
        var analysis = ProgramAnalysis.Analyze(HunkFile.Parse(executable), listing);
        foreach (var warning in analysis.Warnings)
            log.WriteLine($"warning: {warning}");
        var source = CSharpProgramWriter.Write(analysis, Namespace, ClassName, "the launched executable");

        log.WriteLine($"Compiling {analysis.Functions.Count} functions and {analysis.Instructions.Count} instructions.");
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "Translated_" + key,
            [tree],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        Directory.CreateDirectory(CacheDirectory);
        var temporary = cached + ".tmp";
        using (var stream = File.Create(temporary))
        {
            var result = compilation.Emit(stream);
            if (!result.Success)
            {
                var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10);
                throw new InvalidOperationException("The translated code does not compile:\n" + string.Join("\n", errors));
            }
        }

        File.Move(temporary, cached, overwrite: true);
        return Assembly.LoadFile(cached).GetType($"{Namespace}.{ClassName}")!;
    }

    private static string CacheKey(byte[] executable, byte[] listing)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(executable);
        hash.AppendData(listing);
        // A new version of the translator or the runtime makes a new translation.
        hash.AppendData(File.ReadAllBytes(typeof(CSharpProgramWriter).Assembly.Location));
        hash.AppendData(File.ReadAllBytes(typeof(Core).Assembly.Location));
        return Convert.ToHexString(hash.GetHashAndReset())[..24];
    }

    private static IEnumerable<MetadataReference> References()
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return platform
            .Append(typeof(Core).Assembly.Location)
            .Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));
    }
}
