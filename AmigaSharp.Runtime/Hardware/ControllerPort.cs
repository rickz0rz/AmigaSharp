namespace AmigaSharp.Runtime.Hardware;

/// <summary>The device in a controller port.</summary>
public enum ControllerType
{
    Mouse,
    Joystick,
}

/// <summary>The buttons of a mouse or a joystick.</summary>
public enum ControllerButton
{
    /// <summary>The left mouse button, or fire button 1. Pin 6: CIA-A PRA bit 6 (port 1) or bit 7 (port 2).</summary>
    Left,

    /// <summary>The right mouse button, or fire button 2. Pin 9: POTINP DATLY (port 1) or DATRY (port 2).</summary>
    Right,

    /// <summary>The middle mouse button, or fire button 3. Pin 5: POTINP DATLX (port 1) or DATRX (port 2).</summary>
    Middle,
}

/// <summary>The directions of a joystick.</summary>
[Flags]
public enum JoystickDirection
{
    None = 0,
    Up = 1 << 0,
    Down = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
}

/// <summary>
/// A controller port of the Amiga, with a mouse or a joystick. The host thread changes the state, and the program
/// reads it from JOYxDAT, POTINP and CIA-A PRA.
/// </summary>
/// <remarks>
/// A mouse has two 8-bit counters in JOYxDAT: Y in bits 15 to 8 and X in bits 7 to 0. A joystick sets bit 1 for right
/// and bit 9 for left. Bit 0 is down XOR right, and bit 8 is up XOR left, because the switches share the counter
/// inputs.
/// </remarks>
public sealed class ControllerPort
{
    private readonly object _lock = new();
    private int _x;
    private int _y;
    private JoystickDirection _directions;
    private readonly bool[] _buttons = new bool[3];

    public ControllerType Type { get; set; }

    /// <summary>A button changed. The chipset uses it to change the pin of CIA-A.</summary>
    public event Action? ButtonChanged;

    /// <summary>Moves the mouse by a number of counts. A count is about one low-resolution pixel.</summary>
    public void MoveMouse(int dx, int dy)
    {
        lock (_lock)
        {
            _x += dx;
            _y += dy;
        }
    }

    public void SetDirection(JoystickDirection direction, bool on)
    {
        lock (_lock)
            _directions = on ? _directions | direction : _directions & ~direction;
    }

    public void SetButton(ControllerButton button, bool pressed)
    {
        lock (_lock)
            _buttons[(int)button] = pressed;
        ButtonChanged?.Invoke();
    }

    public bool IsPressed(ControllerButton button)
    {
        lock (_lock)
            return _buttons[(int)button];
    }

    /// <summary>The value of JOY0DAT or JOY1DAT.</summary>
    public ushort Data
    {
        get
        {
            lock (_lock)
            {
                if (Type == ControllerType.Mouse)
                    return (ushort)((_y & 0xFF) << 8 | (_x & 0xFF));

                var right = (_directions & JoystickDirection.Right) != 0;
                var left = (_directions & JoystickDirection.Left) != 0;
                var down = (_directions & JoystickDirection.Down) != 0;
                var up = (_directions & JoystickDirection.Up) != 0;
                var value = 0;
                if (right)
                    value |= 1 << 1;
                if (left)
                    value |= 1 << 9;
                if (down ^ right)
                    value |= 1 << 0;
                if (up ^ left)
                    value |= 1 << 8;
                return (ushort)value;
            }
        }
    }

    /// <summary>A write to JOYTEST: it sets bits 7 to 2 of each mouse counter.</summary>
    public void Test(ushort value)
    {
        lock (_lock)
        {
            _x = (_x & 3) | (value & 0xFC);
            _y = (_y & 3) | ((value >> 8) & 0xFC);
        }
    }
}
