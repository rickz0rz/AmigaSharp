using System.Text;

namespace AmigaSharp.Runtime.Input;

/// <summary>
/// The US keymap of the Amiga: converts a raw key event to characters, as console.device RawKeyConvert does. The
/// cursor keys, the function keys and Help give CSI sequences.
/// </summary>
public static class KeyMap
{
    /// <summary>The Control Sequence Introducer of the Amiga console.</summary>
    public const byte Csi = 0x9B;

    // The characters of raw keys $00 to $3F without Shift and with Shift. A space means no character.
    private const string Normal = "`1234567890-=\\ 0qwertyuiop[] 123asdfghjkl;'  456 zxcvbnm,./ .789";
    private const string Shifted = "~!@#$%^&*()_+| 0QWERTYUIOP{} 123ASDFGHJKL:\"  456 ZXCVBNM<>? .789";

    /// <summary>The characters of a key event, or no characters for an event that has none, for example a key that goes up.</summary>
    public static byte[] Convert(ushort code, Qualifier qualifier)
    {
        if ((code & InputQueue.KeyUp) != 0)
            return [];

        var key = (byte)code;
        var shift = (qualifier & Qualifier.Shift) != 0;
        switch (key)
        {
            case RawKey.Space: return [(byte)' '];
            case RawKey.Backspace: return [0x08];
            case RawKey.Tab: return shift ? [Csi, (byte)'Z'] : [0x09];
            case RawKey.Return or RawKey.KeypadEnter: return [0x0D];
            case RawKey.Escape: return [0x1B];
            case RawKey.Delete: return [0x7F];
            case RawKey.KeypadMinus: return [(byte)'-'];
            case RawKey.Up: return shift ? Sequence("T") : Sequence("A");
            case RawKey.Down: return shift ? Sequence("S") : Sequence("B");
            case RawKey.Right: return shift ? Sequence(" @") : Sequence("C");
            case RawKey.Left: return shift ? Sequence(" A") : Sequence("D");
            case >= RawKey.F1 and <= RawKey.F10: return Sequence($"{key - RawKey.F1 + (shift ? 10 : 0)}~");
            case RawKey.Help: return Sequence("?~");
            case 0x5A: return [(byte)'('];
            case 0x5B: return [(byte)')'];
            case 0x5C: return [(byte)'/'];
            case 0x5D: return [(byte)'*'];
            case 0x5E: return [(byte)'+'];
        }

        if (key >= Normal.Length || Normal[key] == ' ')
            return [];

        var letter = char.IsAsciiLetter(Normal[key]);
        var upper = shift ^ (letter && (qualifier & Qualifier.CapsLock) != 0);
        var character = (byte)(upper ? Shifted[key] : Normal[key]);
        if ((qualifier & Qualifier.Control) != 0 && letter)
            character &= 0x1F;
        return [character];
    }

    private static byte[] Sequence(string text) => [Csi, .. Encoding.ASCII.GetBytes(text)];
}
