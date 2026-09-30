using AmigaSharp.Runtime.Input;

namespace AmigaSharp.Host;

/// <summary>The names of keys for the option --press, and their raw key codes.</summary>
public static class KeyNames
{
    public static byte? RawKey(string name)
    {
        name = name.ToLowerInvariant();
        if (name.Length == 1)
        {
            var c = name[0];
            var rows = new[] { ("qwertyuiop", 0x10), ("asdfghjkl", 0x20), ("zxcvbnm", 0x31) };
            foreach (var (row, start) in rows)
            {
                var index = row.IndexOf(c);
                if (index >= 0)
                    return (byte)(start + index);
            }

            if (c is >= '1' and <= '9')
                return (byte)(c - '0');
            if (c == '0')
                return 0x0A;
        }

        if (name.Length is 2 or 3 && name[0] == 'f' && int.TryParse(name[1..], out var number) && number is >= 1 and <= 10)
            return (byte)(Runtime.Input.RawKey.F1 + number - 1);

        return name switch
        {
            "space" => Runtime.Input.RawKey.Space,
            "return" or "enter" => Runtime.Input.RawKey.Return,
            "escape" or "esc" => Runtime.Input.RawKey.Escape,
            "backspace" => Runtime.Input.RawKey.Backspace,
            "tab" => Runtime.Input.RawKey.Tab,
            "delete" => Runtime.Input.RawKey.Delete,
            "help" => Runtime.Input.RawKey.Help,
            "up" => Runtime.Input.RawKey.Up,
            "down" => Runtime.Input.RawKey.Down,
            "left" => Runtime.Input.RawKey.Left,
            "right" => Runtime.Input.RawKey.Right,
            _ => null,
        };
    }
}
