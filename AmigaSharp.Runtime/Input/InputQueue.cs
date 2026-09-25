using System.Collections.Concurrent;

namespace AmigaSharp.Runtime.Input;

/// <summary>The classes of input events (devices/inputevent.h).</summary>
public static class InputClass
{
    public const byte RawKey = 1;
    public const byte RawMouse = 2;
    public const byte Timer = 6;
}

/// <summary>The qualifier bits of input events (devices/inputevent.h).</summary>
[Flags]
public enum Qualifier : ushort
{
    None = 0,
    LeftShift = 1 << 0,
    RightShift = 1 << 1,
    CapsLock = 1 << 2,
    Control = 1 << 3,
    LeftAlt = 1 << 4,
    RightAlt = 1 << 5,
    LeftAmiga = 1 << 6,
    RightAmiga = 1 << 7,
    NumericPad = 1 << 8,
    Repeat = 1 << 9,

    Shift = LeftShift | RightShift,
    Alt = LeftAlt | RightAlt,
}

/// <summary>An input event: the class, the code (for a key, the raw key code with bit 7 set when it goes up) and the qualifiers.</summary>
public readonly record struct InputEventData(byte Class, ushort Code, Qualifier Qualifier);

/// <summary>
/// The events from the host keyboard. The host thread adds events, and input.device takes them on the thread of the
/// program. The queue keeps the state of the qualifier keys.
/// </summary>
public sealed class InputQueue
{
    /// <summary>Bit 7 of the code of a key event: the key goes up.</summary>
    public const ushort KeyUp = 0x80;

    private readonly ConcurrentQueue<InputEventData> _events = new();
    private Qualifier _qualifier;

    public bool IsEmpty => _events.IsEmpty;

    public bool TryTake(out InputEventData value) => _events.TryDequeue(out value);

    /// <summary>Adds a key event with a raw key code of the Amiga keyboard.</summary>
    public void PostRawKey(byte code, bool up)
    {
        var qualifier = QualifierOf(code);
        if (qualifier != Qualifier.None)
        {
            if (code == RawKey.CapsLock)
            {
                // Caps Lock changes each time that the key goes down.
                if (!up)
                    _qualifier ^= Qualifier.CapsLock;
            }
            else if (up)
            {
                _qualifier &= ~qualifier;
            }
            else
            {
                _qualifier |= qualifier;
            }
        }

        var keypad = RawKey.IsNumericPad(code) ? Qualifier.NumericPad : Qualifier.None;
        _events.Enqueue(new InputEventData(InputClass.RawKey, (ushort)(code | (up ? KeyUp : 0)), _qualifier | keypad));
    }

    private static Qualifier QualifierOf(byte code) => code switch
    {
        RawKey.LeftShift => Qualifier.LeftShift,
        RawKey.RightShift => Qualifier.RightShift,
        RawKey.CapsLock => Qualifier.CapsLock,
        RawKey.Control => Qualifier.Control,
        RawKey.LeftAlt => Qualifier.LeftAlt,
        RawKey.RightAlt => Qualifier.RightAlt,
        RawKey.LeftAmiga => Qualifier.LeftAmiga,
        RawKey.RightAmiga => Qualifier.RightAmiga,
        _ => Qualifier.None,
    };
}

/// <summary>The raw key codes of the Amiga keyboard that are not letters, digits or punctuation.</summary>
public static class RawKey
{
    public const byte Space = 0x40;
    public const byte Backspace = 0x41;
    public const byte Tab = 0x42;
    public const byte KeypadEnter = 0x43;
    public const byte Return = 0x44;
    public const byte Escape = 0x45;
    public const byte Delete = 0x46;
    public const byte KeypadMinus = 0x4A;
    public const byte Up = 0x4C;
    public const byte Down = 0x4D;
    public const byte Right = 0x4E;
    public const byte Left = 0x4F;
    public const byte F1 = 0x50;
    public const byte F10 = 0x59;
    public const byte Help = 0x5F;
    public const byte LeftShift = 0x60;
    public const byte RightShift = 0x61;
    public const byte CapsLock = 0x62;
    public const byte Control = 0x63;
    public const byte LeftAlt = 0x64;
    public const byte RightAlt = 0x65;
    public const byte LeftAmiga = 0x66;
    public const byte RightAmiga = 0x67;

    public static bool IsNumericPad(byte code) =>
        code is 0x0F or 0x1D or 0x1E or 0x1F or 0x2D or 0x2E or 0x2F or 0x3C or 0x3D or 0x3E or 0x3F or KeypadEnter
            or KeypadMinus or >= 0x5A and <= 0x5E;
}
