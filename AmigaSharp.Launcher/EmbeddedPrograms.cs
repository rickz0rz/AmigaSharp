using System.Security.Cryptography;
using AmigaSharp.Runtime;

namespace AmigaSharp.Launcher;

/// <summary>
/// The translations that the build compiled into the launcher. The launcher uses one when the executable to run has the
/// SHA-256 of the executable that the build translated. A native (AOT) launcher can then run the translation, and it
/// does not need the interpreter for that program.
/// </summary>
/// <remarks>
/// Build the launcher with the MSBuild properties EmbeddedProgram (the executable) and EmbeddedListing (its vasm
/// listing, optional), for example with scripts/publish.sh. The build then runs the translator and generates the body
/// of <see cref="Register"/>. Without these properties, the launcher has no translations.
/// </remarks>
public static partial class EmbeddedPrograms
{
    private static readonly Dictionary<string, Func<Core, TranslatedProgram>> Programs = Create();

    /// <summary>The number of translations in the launcher.</summary>
    public static int Count => Programs.Count;

    /// <summary>Finds the translation of the executable. Returns false if the launcher does not have one.</summary>
    public static bool TryGet(byte[] executable, out Func<Core, TranslatedProgram> create) =>
        Programs.TryGetValue(Convert.ToHexString(SHA256.HashData(executable)), out create!);

    private static Dictionary<string, Func<Core, TranslatedProgram>> Create()
    {
        var programs = new Dictionary<string, Func<Core, TranslatedProgram>>(StringComparer.OrdinalIgnoreCase);
        Register(programs);
        return programs;
    }

    /// <summary>Adds the translations, keyed by the SHA-256 of the executable in hexadecimal. The build generates it.</summary>
    static partial void Register(Dictionary<string, Func<Core, TranslatedProgram>> programs);
}
