using System.Text;
using System.Text.RegularExpressions;

namespace AmigaSharp.Runtime.Dos;

/// <summary>
/// The wildcard patterns of AmigaDOS: <c>#?</c> is any text, <c>?</c> is one character, <c>#x</c> is zero or more of
/// x, <c>(a|b)</c> is a choice, <c>[a-z]</c> is a set of characters, and <c>'</c> makes the next character normal.
/// The patterns do not match case.
/// </summary>
public static class AmigaPattern
{
    public static bool HasWildcards(string text) => text.IndexOfAny(['#', '?', '(', '[', '|', '~']) >= 0;

    public static Regex ToRegex(string pattern)
    {
        var text = new StringBuilder("^");
        var position = 0;
        while (position < pattern.Length)
            text.Append(Atom(pattern, ref position));
        text.Append('$');
        return new Regex(text.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Atom(string pattern, ref int position)
    {
        var c = pattern[position++];
        switch (c)
        {
            case '?':
                return ".";
            case '#':
                if (position >= pattern.Length)
                    return Regex.Escape("#");
                return $"(?:{Atom(pattern, ref position)})*";
            case '(':
                return "(?:";
            case ')':
                return ")";
            case '|':
                return "|";
            case '[':
            {
                var end = pattern.IndexOf(']', position);
                if (end < 0)
                    return Regex.Escape("[");
                var set = pattern[position..end];
                position = end + 1;
                return "[" + set.Replace("\\", "\\\\") + "]";
            }
            case '\'':
                return position < pattern.Length ? Regex.Escape(pattern[position++].ToString()) : "'";
            default:
                return Regex.Escape(c.ToString());
        }
    }
}
