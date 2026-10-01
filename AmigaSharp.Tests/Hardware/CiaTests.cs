using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Tests.Hardware;

public class CiaTests
{
    private const uint CiaA = 0xBFE001;
    private const byte Start = 0x01;
    private const byte OneShot = 0x08;
    private const byte ForceLoad = 0x10;

    private readonly ManualClock _clock = new();
    private readonly Memory _memory = new();
    private readonly Chipset _chipset;

    public CiaTests()
    {
        _chipset = new Chipset(_clock, _memory);
        _memory.Hardware = _chipset;
    }

    [Fact]
    public void ContinuousTimer_CountsDownAtTheEClock_AndLoadsTheLatchAtTheUnderflow()
    {
        SetTimerA(100);
        Write(CiaRegister.Cra, Start | ForceLoad);

        MoveTo(40);
        Assert.Equal(60, TimerA());
        Assert.Equal(0, Read(CiaRegister.Icr));

        // The first underflow comes after 101 cycles: from 100 down to 0, and then one more.
        MoveTo(101);
        Assert.Equal(100, TimerA());
        Assert.Equal(CiaInterrupt.TimerA, Read(CiaRegister.Icr));
        Assert.Equal(0, Read(CiaRegister.Icr));

        MoveTo(101 + 3 * 101 + 1);
        Assert.Equal(99, TimerA());
        Assert.Equal(Start, Read(CiaRegister.Cra) & Start);
    }

    [Fact]
    public void OneShotTimer_StartsWhenTheHighByteIsWritten_AndStopsAtTheUnderflow()
    {
        Write(CiaRegister.Cra, OneShot);
        SetTimerA(50);
        Assert.Equal(Start, Read(CiaRegister.Cra) & Start);

        MoveTo(51);

        Assert.Equal(CiaInterrupt.TimerA, Read(CiaRegister.Icr));
        Assert.Equal(0, Read(CiaRegister.Cra) & Start);
        MoveTo(500);
        Assert.Equal(50, TimerA());
    }

    [Fact]
    public void TimerB_CanCountTheUnderflowsOfTimerA()
    {
        SetTimerA(9);
        Write(CiaRegister.TbLow, 3);
        Write(CiaRegister.TbHigh, 0);
        Write(CiaRegister.Cra, Start | ForceLoad);
        Write(CiaRegister.Crb, 0x40 | Start | ForceLoad);

        // Timer A underflows each 10 cycles. Timer B counts 3, 2, 1, 0 and underflows at the fourth.
        MoveTo(35);
        Assert.Equal(0, Read(CiaRegister.TbLow));
        MoveTo(45);
        Assert.Equal(CiaInterrupt.TimerA | CiaInterrupt.TimerB, Read(CiaRegister.Icr));
    }

    [Fact]
    public void EnabledFlag_RequestsThePortsInterrupt_AndIcrShowsBit7()
    {
        Write(CiaRegister.Icr, CiaInterrupt.SetClear | CiaInterrupt.TimerA);
        SetTimerA(10);
        Write(CiaRegister.Cra, Start | ForceLoad);

        MoveTo(20);
        _chipset.CiaA.Update();

        Assert.NotEqual(0, _chipset.Custom.Intreq & (1 << InterruptBit.Ports));
        Assert.Equal(CiaInterrupt.SetClear | CiaInterrupt.TimerA, Read(CiaRegister.Icr));
    }

    [Fact]
    public void TimeOfDay_CanBeSet_AndAReadOfTheHighByteLatchesTheValue()
    {
        Write(CiaRegister.TodHigh, 0x01);
        Write(CiaRegister.TodMiddle, 0x02);
        MoveToFrame(5);
        // The counter stops from the write of the high byte to the write of the low byte.
        Write(CiaRegister.TodLow, 0x03);
        Assert.Equal(0x010203, TimeOfDay());

        MoveToFrame(7);
        Assert.Equal(0x01, Read(CiaRegister.TodHigh));
        MoveToFrame(300);
        Assert.Equal(0x02, Read(CiaRegister.TodMiddle));
        Assert.Equal(0x05, Read(CiaRegister.TodLow));
        Assert.Equal(0x010203 + 295, TimeOfDay());
    }

    [Fact]
    public void TimeOfDayAlarm_SetsItsFlag()
    {
        Write(CiaRegister.Crb, 0x80);
        Write(CiaRegister.TodHigh, 0);
        Write(CiaRegister.TodMiddle, 0);
        Write(CiaRegister.TodLow, 10);
        Write(CiaRegister.Crb, 0);
        Write(CiaRegister.TodHigh, 0);
        Write(CiaRegister.TodMiddle, 0);
        Write(CiaRegister.TodLow, 0);

        MoveToFrame(9);
        Assert.Equal(0, Read(CiaRegister.Icr) & CiaInterrupt.Alarm);
        MoveToFrame(10);
        Assert.Equal(CiaInterrupt.Alarm, Read(CiaRegister.Icr) & CiaInterrupt.Alarm);
    }

    [Fact]
    public void Keyboard_SendsTheRotatedInvertedCode_AndWaitsForTheHandshake()
    {
        _chipset.Keyboard.Post(0x45, up: false);
        _chipset.Keyboard.Post(0x45, up: true);

        MoveTo(1000);
        _chipset.Keyboard.Update();
        // $45 = 0100 0101. Rotated left: 1000 1010. Inverted: 0111 0101.
        Assert.Equal(0x75, Read(CiaRegister.Sdr));

        MoveTo(3000);
        _chipset.Keyboard.Update();
        Assert.Equal(0x75, Read(CiaRegister.Sdr));

        Assert.Equal(CiaInterrupt.SerialPort, Read(CiaRegister.Icr));
        MoveTo(4000);
        _chipset.Keyboard.Update();
        // $C5 (key up) = 1100 0101. Rotated left: 1000 1011. Inverted: 0111 0100.
        Assert.Equal(0x74, Read(CiaRegister.Sdr));
    }

    private void SetTimerA(int value)
    {
        Write(CiaRegister.TaLow, (byte)value);
        Write(CiaRegister.TaHigh, (byte)(value >> 8));
    }

    private int TimerA() => Read(CiaRegister.TaHigh) << 8 | Read(CiaRegister.TaLow);

    private int TimeOfDay() => Read(CiaRegister.TodHigh) << 16 | Read(CiaRegister.TodMiddle) << 8 | Read(CiaRegister.TodLow);

    private void MoveTo(long eClockCycles) =>
        _clock.Elapsed = TimeSpan.FromSeconds((eClockCycles + 0.5) / VideoStandard.Ntsc.EClockHz);

    private void MoveToFrame(int frame) =>
        _clock.Elapsed = TimeSpan.FromSeconds((frame + 0.5) * Beam.ColorClocksPerLine * VideoStandard.Ntsc.LinesPerFrame / VideoStandard.Ntsc.ColorClockHz);

    private byte Read(int register) => _memory.Read8(CiaA + (uint)register * 0x100);

    private void Write(int register, byte value) => _memory.Write8(CiaA + (uint)register * 0x100, value);
}
