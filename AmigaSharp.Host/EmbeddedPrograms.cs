using System.Security.Cryptography;
using AmigaSharp.Runtime;

namespace AmigaSharp.Host;

/// <summary>
/// The translations that the build compiled into the launcher. The launcher uses one when the executable to run has the
/// SHA-256 of the executable that the build translated. A native (AOT) launcher can then run the translation, and it
/// does not need the interpreter for that program.
/// </summary>
/// <remarks>
/// Build a launcher with the MSBuild properties EmbeddedProgram (the executable) and EmbeddedListing (its vasm
/// listing, optional), for example with scripts/publish.sh. EmbeddedProgram.targets then runs the translator, and it
/// generates a module initializer that calls <see cref="Add"/>. Without these properties, the launcher has no
/// translations.
/// </remarks>
public static class EmbeddedPrograms
{
    private static readonly Dictionary<string, Func<Core, TranslatedProgram>> Programs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of translations in the launcher.</summary>
    public static int Count => Programs.Count;

    /// <summary>Adds a translation, keyed by the SHA-256 of the executable in hexadecimal. The build calls it.</summary>
    public static void Add(string sha256, Func<Core, TranslatedProgram> create) => Programs[sha256] = create;

    /// <summary>Finds the translation of the executable. Returns false if the launcher does not have one.</summary>
    public static bool TryGet(byte[] executable, out Func<Core, TranslatedProgram> create) =>
        Programs.TryGetValue(Convert.ToHexString(SHA256.HashData(executable)), out create!);
}
