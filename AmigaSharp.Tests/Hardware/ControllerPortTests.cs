using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

public class ControllerPortTests
{
    private const uint Joy0dat = 0xDFF00A;
    private const uint Joy1dat = 0xDFF00C;
    private const uint Potgor = 0xDFF016;
    private const uint Potgo = 0xDFF034;
    private const uint Joytest = 0xDFF036;
    private const uint CiaAPra = 0xBFE001;

    private readonly Memory _memory = new();
    private readonly Chipset _chipset;

    public ControllerPortTests()
    {
        _chipset = new Chipset(new ManualClock(), _memory);
        _memory.Hardware = _chipset;
    }

    private ControllerPort Mouse => _chipset.Custom.Ports[0];
    private ControllerPort Joystick => _chipset.Custom.Ports[1];

    [Fact]
    public void Mouse_CountsInJoy0dat_AndTheCountersWrap()
    {
        Mouse.MoveMouse(5, -3);
        Assert.Equal(0xFD05, _memory.Read16(Joy0dat));

        Mouse.MoveMouse(300, 0);
        Assert.Equal(0xFD31, _memory.Read16(Joy0dat));
    }

    [Fact]
    public void Joytest_SetsTheHighBitsOfTheCounters()
    {
        Mouse.MoveMouse(3, 2);

        _memory.Write16(Joytest, 0x4080);

        Assert.Equal(0x4283, _memory.Read16(Joy0dat));
    }

    [Theory]
    [InlineData(JoystickDirection.Right, 0x0002 | 0x0001)]
    [InlineData(JoystickDirection.Left, 0x0200 | 0x0100)]
    [InlineData(JoystickDirection.Down, 0x0001)]
    [InlineData(JoystickDirection.Up, 0x0100)]
    [InlineData(JoystickDirection.Up | JoystickDirection.Right, 0x0100 | 0x0002 | 0x0001)]
    [InlineData(JoystickDirection.Down | JoystickDirection.Left, 0x0200 | 0x0100 | 0x0001)]
    public void Joystick_EncodesTheSwitchesInJoy1dat(JoystickDirection directions, int expected)
    {
        Joystick.SetDirection(directions, true);

        Assert.Equal(expected, _memory.Read16(Joy1dat));
    }

    [Fact]
    public void FireButtons_PullTheirCiaAPinsLow()
    {
        Assert.Equal(0xC0, _memory.Read8(CiaAPra) & 0xC0);

        Mouse.SetButton(ControllerButton.Left, true);
        Assert.Equal(0x80, _memory.Read8(CiaAPra) & 0xC0);

        Joystick.SetButton(ControllerButton.Left, true);
        Mouse.SetButton(ControllerButton.Left, false);
        Assert.Equal(0x40, _memory.Read8(CiaAPra) & 0xC0);
    }

    [Fact]
    public void RightAndMiddleButtons_PullTheirPotPinsLow_AlsoWhenThePinsAreOutputs()
    {
        Assert.Equal(0x5500, _memory.Read16(Potgor) & 0x5500);

        Mouse.SetButton(ControllerButton.Right, true);
        Joystick.SetButton(ControllerButton.Middle, true);
        Assert.Equal(0x4100, _memory.Read16(Potgor) & 0x5500);

        // Outputs that drive the pins high, as a program sets them to read the right mouse button.
        _memory.Write16(Potgo, 0xFF00);
        Assert.Equal(0x4100, _memory.Read16(Potgor) & 0x5500);

        // An output that drives a pin low reads low without a button.
        _memory.Write16(Potgo, 0xBF00);
        Assert.Equal(0x0100, _memory.Read16(Potgor) & 0x5500);
    }
}
