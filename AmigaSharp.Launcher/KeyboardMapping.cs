using AmigaSharp.Runtime.Input;
using Silk.NET.SDL;

namespace AmigaSharp.Launcher;

/// <summary>Converts the keys of the host (SDL scancodes) to the raw key codes of the Amiga keyboard, US layout.</summary>
public static class KeyboardMapping
{
    private static readonly Dictionary<Scancode, byte> Keys = Build();

    public static bool TryGetRawKey(Scancode scancode, out byte rawKey) => Keys.TryGetValue(scancode, out rawKey);

    private static Dictionary<Scancode, byte> Build()
    {
        var keys = new Dictionary<Scancode, byte>
        {
            [Scancode.ScancodeGrave] = 0x00,
            [Scancode.ScancodeMinus] = 0x0B,
            [Scancode.ScancodeEquals] = 0x0C,
            [Scancode.ScancodeBackslash] = 0x0D,
            [Scancode.ScancodeLeftbracket] = 0x1A,
            [Scancode.ScancodeRightbracket] = 0x1B,
            [Scancode.ScancodeSemicolon] = 0x29,
            [Scancode.ScancodeApostrophe] = 0x2A,
            [Scancode.ScancodeComma] = 0x38,
            [Scancode.ScancodePeriod] = 0x39,
            [Scancode.ScancodeSlash] = 0x3A,
            [Scancode.ScancodeSpace] = RawKey.Space,
            [Scancode.ScancodeBackspace] = RawKey.Backspace,
            [Scancode.ScancodeTab] = RawKey.Tab,
            [Scancode.ScancodeReturn] = RawKey.Return,
            [Scancode.ScancodeEscape] = RawKey.Escape,
            [Scancode.ScancodeDelete] = RawKey.Delete,
            [Scancode.ScancodeUp] = RawKey.Up,
            [Scancode.ScancodeDown] = RawKey.Down,
            [Scancode.ScancodeRight] = RawKey.Right,
            [Scancode.ScancodeLeft] = RawKey.Left,
            // The Amiga has Help where a PC has F11.
            [Scancode.ScancodeF11] = RawKey.Help,
            [Scancode.ScancodeLshift] = RawKey.LeftShift,
            [Scancode.ScancodeRshift] = RawKey.RightShift,
            [Scancode.ScancodeCapslock] = RawKey.CapsLock,
            [Scancode.ScancodeLctrl] = RawKey.Control,
            [Scancode.ScancodeRctrl] = RawKey.Control,
            [Scancode.ScancodeLalt] = RawKey.LeftAlt,
            [Scancode.ScancodeRalt] = RawKey.RightAlt,
            [Scancode.ScancodeLgui] = RawKey.LeftAmiga,
            [Scancode.ScancodeRgui] = RawKey.RightAmiga,
            [Scancode.ScancodeKPEnter] = RawKey.KeypadEnter,
            [Scancode.ScancodeKPMinus] = RawKey.KeypadMinus,
            [Scancode.ScancodeKPDivide] = 0x5C,
            [Scancode.ScancodeKPMultiply] = 0x5D,
            [Scancode.ScancodeKPPlus] = 0x5E,
            [Scancode.ScancodeKP0] = 0x0F,
            [Scancode.ScancodeKPPeriod] = 0x3C,
        };

        // 1 to 9 and 0 are $01 to $0A.
        for (var digit = 0; digit < 9; digit++)
            keys[Scancode.Scancode1 + digit] = (byte)(0x01 + digit);
        keys[Scancode.Scancode0] = 0x0A;

        // The letters are in rows: Q to P from $10, A to L from $20, Z to M from $31.
        foreach (var (row, start) in new[] { ("QWERTYUIOP", 0x10), ("ASDFGHJKL", 0x20), ("ZXCVBNM", 0x31) })
        {
            for (var i = 0; i < row.Length; i++)
                keys[Scancode.ScancodeA + (row[i] - 'A')] = (byte)(start + i);
        }

        for (var key = 0; key < 10; key++)
            keys[Scancode.ScancodeF1 + key] = (byte)(RawKey.F1 + key);

        // The keypad: 1 to 3 are $1D to $1F, 4 to 6 are $2D to $2F, and 7 to 9 are $3D to $3F.
        for (var digit = 1; digit <= 9; digit++)
            keys[Scancode.ScancodeKP1 + (digit - 1)] = (byte)((digit - 1) / 3 switch { 0 => 0x1D, 1 => 0x2D, _ => 0x3D } + (digit - 1) % 3);
        return keys;
    }
}
