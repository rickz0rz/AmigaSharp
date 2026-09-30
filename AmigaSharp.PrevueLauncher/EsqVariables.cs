using System.Security.Cryptography;
using AmigaSharp.Runtime.Loader;
using AmigaSharp.Translator;

namespace AmigaSharp.PrevueLauncher;

/// <summary>
/// The addresses of some variables of ESQ, for the state of Prevue (see <see cref="PrevueState"/>). The addresses come
/// from the vasm listing, or from a table for the known build of ESQ when the launcher has no listing.
/// </summary>
public sealed class EsqVariables
{
    /// <summary>The SHA-256 of the build of ESQ that <see cref="KnownOffsets"/> is for.</summary>
    public const string KnownSha256 = "6BD4760D1CF0706297EF169461ED0D7B7F0B079110A78E34D89223499E7C2FA2";

    public const string CommandCount = "_SCRIPT_CtrlCmdCount";
    public const string ChecksumErrorCount = "_SCRIPT_CtrlCmdChecksumErrorCount";
    public const string ParserState = "_SCRIPT_CTRL_STATE";
    public const string BufferHead = "_CTRL_H";
    public const string BufferTail = "_CTRL_HPreviousSample";
    public const string LoadedLogo = "_ESQIFF_LogoBrushListHead";
    public const string LogoListLine = "_ESQIFF_LogoListLineIndex";
    public const string LogoListData = "_Global_REF_LONG_DF0_LOGO_LST_DATA";
    public const string LogoListSize = "_Global_REF_LONG_DF0_LOGO_LST_FILESIZE";

    /// <summary>
    /// The section (hunk) and the offset of each variable in the known build of ESQ. A test compares the table with the
    /// listing when the target program is built.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (int Section, uint Offset)> KnownOffsets =
        new Dictionary<string, (int, uint)>
        {
            [CommandCount] = (1, 0x0BF12),
            [ChecksumErrorCount] = (1, 0x0BF14),
            [ParserState] = (1, 0x0BF0E),
            [BufferHead] = (1, 0x0A32C),
            [BufferTail] = (1, 0x0A32E),
            [LoadedLogo] = (1, 0x05A88),
            [LogoListLine] = (1, 0x0A82A),
            [LogoListData] = (1, 0x05AE8),
            [LogoListSize] = (1, 0x05AE4),
        };

    private readonly Dictionary<string, uint> _addresses;

    private EsqVariables(Dictionary<string, uint> addresses) => _addresses = addresses;

    /// <summary>The address of a variable in the memory of the Amiga.</summary>
    public uint this[string name] => _addresses[name];

    /// <summary>
    /// Finds the addresses for the executable. Returns null if the executable is not the known build of ESQ and the
    /// launcher has no listing, or if the listing does not have the variables.
    /// </summary>
    public static EsqVariables? Find(byte[] executable, string? listing)
    {
        IReadOnlyDictionary<string, (int Section, uint Offset)> offsets;
        if (listing != null)
        {
            var symbols = VasmListing.Read(listing).Symbols;
            var fromListing = new Dictionary<string, (int, uint)>();
            foreach (var name in KnownOffsets.Keys)
            {
                if (!symbols.TryGetValue(name, out var symbol) || symbol.Section is not { } section)
                    return null;
                fromListing[name] = (section, symbol.Value);
            }

            offsets = fromListing;
        }
        else if (Convert.ToHexString(SHA256.HashData(executable)) == KnownSha256)
        {
            offsets = KnownOffsets;
        }
        else
        {
            return null;
        }

        return At(HunkLayout.Assign(HunkFile.Parse(executable)), offsets);
    }

    /// <summary>The variables of the known build of ESQ, with its hunks at these addresses.</summary>
    public static EsqVariables? FromBases(uint[] bases) => At(bases, KnownOffsets);

    private static EsqVariables? At(uint[] bases, IReadOnlyDictionary<string, (int Section, uint Offset)> offsets)
    {
        var addresses = new Dictionary<string, uint>();
        foreach (var (name, (section, offset)) in offsets)
        {
            if (section >= bases.Length)
                return null;
            addresses[name] = bases[section] + offset;
        }

        return new EsqVariables(addresses);
    }
}
