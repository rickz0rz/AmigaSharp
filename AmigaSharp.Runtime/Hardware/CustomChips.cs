namespace AmigaSharp.Runtime.Hardware;

/// <summary>The offsets of the custom chip registers from $DFF000 (hardware/custom.h).</summary>
public static class CustomRegister
{
    public const uint Base = 0xDF_F000;

    public const int Bltddat = 0x000;
    public const int Dmaconr = 0x002;
    public const int Vposr = 0x004;
    public const int Vhposr = 0x006;
    public const int Joy0dat = 0x00A;
    public const int Joy1dat = 0x00C;
    public const int Adkconr = 0x010;
    public const int Pot0dat = 0x012;
    public const int Pot1dat = 0x014;
    public const int Potgor = 0x016;
    public const int Serdatr = 0x018;
    public const int Dskbytr = 0x01A;
    public const int Intenar = 0x01C;
    public const int Intreqr = 0x01E;
    public const int Serdat = 0x030;
    public const int Potgo = 0x034;
    public const int Joytest = 0x036;
    public const int Serper = 0x032;
    public const int Cop1lc = 0x080;
    public const int Cop2lc = 0x084;
    public const int Copjmp1 = 0x088;
    public const int Copjmp2 = 0x08A;
    public const int Diwstrt = 0x08E;
    public const int Diwstop = 0x090;
    public const int Ddfstrt = 0x092;
    public const int Ddfstop = 0x094;
    public const int Dmacon = 0x096;
    public const int Intena = 0x09A;
    public const int Intreq = 0x09C;
    public const int Adkcon = 0x09E;
    public const int Aud0lc = 0x0A0;
    public const int Aud0len = 0x0A4;
    public const int Aud0per = 0x0A6;
    public const int AudioChannelSize = 0x10;
    public const int Bpl1pt = 0x0E0;
    public const int Bplcon0 = 0x100;
    public const int Bplcon1 = 0x102;
    public const int Bplcon2 = 0x104;
    public const int Bpl1mod = 0x108;
    public const int Bpl2mod = 0x10A;
    public const int Deniseid = 0x07C;
    public const int Color00 = 0x180;
}

/// <summary>The bits of INTENA and INTREQ (hardware/intbits.h).</summary>
public static class InterruptBit
{
    public const int Tbe = 0;
    public const int DiskBlock = 1;
    public const int Soft = 2;
    public const int Ports = 3;
    public const int Copper = 4;
    public const int VerticalBlank = 5;
    public const int Blitter = 6;
    public const int Audio0 = 7;
    public const int Rbf = 11;
    public const int DiskSync = 12;
    public const int External = 13;
    public const int Enable = 14;

    /// <summary>The interrupt level of the 68000 for each bit.</summary>
    public static int Level(int bit) => bit switch
    {
        <= 2 => 1,
        3 => 2,
        <= 6 => 3,
        <= 10 => 4,
        <= 12 => 5,
        _ => 6,
    };
}

/// <summary>
/// The custom chips (Agnus, Denise and Paula) as registers. The model stores each write, so the display and the
/// devices can read the values. DMACON, INTENA, INTREQ and ADKCON use the set/clear bit 15. A read of a register that
/// the model does not have throws <see cref="HardwareAccessException"/>.
/// </summary>
/// <remarks>
/// <see cref="Update"/> makes the interrupt requests that time causes: VERTB at the start of each frame, an audio
/// interrupt each time that a channel with DMA starts its buffer again, and RBF when a serial byte arrives.
/// </remarks>
public sealed class CustomChips
{
    private const ushort DmaEnable = 0x0200;

    private readonly Beam _beam;
    private readonly ushort[] _registers = new ushort[0x100];
    private readonly AudioTimer[] _audio = new AudioTimer[4];

    /// <summary>The number of late audio interrupts that Paula keeps before it drops the oldest.</summary>
    private const int MaximumAudioBacklog = 4;
    private long _lastFrame;

    public CustomChips(Beam beam)
    {
        _beam = beam;
        Serial = new SerialPort(beam);
    }

    /// <summary>The Agnus ID in VPOSR bits 14 to 8. $30 is the ECS Agnus (8372) for NTSC.</summary>
    public int AgnusId { get; set; } = 0x30;

    /// <summary>The Denise ID in the low byte of DENISEID. $FC is the ECS Denise (8373), and $F8 is Lisa (AGA).</summary>
    public int DeniseId { get; set; } = 0xFC;

    public SerialPort Serial { get; }

    /// <summary>The controller ports: a mouse in port 1 and a joystick in port 2, as usual.</summary>
    public ControllerPort[] Ports { get; } =
        [new ControllerPort { Type = ControllerType.Mouse }, new ControllerPort { Type = ControllerType.Joystick }];

    /// <summary>The state of an audio channel for its interrupt: the start of the DMA and the interrupts already made.</summary>
    private struct AudioTimer
    {
        public bool Running;
        public long StartClock;
        public long Interrupts;

        /// <summary>
        /// The time of the channel in color clocks, as the program sees it: each request moves it by one interval. It
        /// does not move for the interrupts that the backlog drops.
        /// </summary>
        public long SampleClock;
    }

    /// <summary>The value that the program last wrote to the register.</summary>
    public ushort this[int offset] => _registers[offset >> 1];

    /// <summary>The program wrote a word to SERDAT.</summary>
    public event Action<ushort>? SerialTransmit;

    /// <summary>The program wrote to COPJMP1 or COPJMP2.</summary>
    public event Action<int>? CopperJump;

    /// <summary>
    /// A frame ended. The display runs the copper and makes the picture. The first value is true for a long frame, and
    /// the second is true if the host must show the picture.
    /// </summary>
    public event Action<bool, bool>? FrameEnded;

    /// <summary>A frame started. The value is true for a long frame.</summary>
    public event Action<bool>? FrameStarted;

    /// <summary>A write to a register, before it takes effect. The display uses it to make the lines before the write.</summary>
    public event Action<int, ushort>? RegisterWritten;

    /// <summary>The program wrote DSKLEN.</summary>
    public event Action<ushort>? DiskLengthWritten;

    /// <summary>DMACON changed. A transfer that waits for its DMA can start.</summary>
    public event Action? DmaChanged;

    /// <summary>The time moved at a safe point. The display makes the lines up to the beam.</summary>
    public event Action? BeamMoved;

    /// <summary>
    /// True for a long frame (VPOSR bit 15). With interlace (BPLCON0 bit 2), long and short frames alternate.
    /// </summary>
    public bool LongFrame { get; private set; } = true;

    /// <summary>The number of frames that ended with no picture, because the program did not reach a safe point.</summary>
    public long FramesDropped { get; private set; }

    /// <summary>The blitter. It starts when the program writes BLTSIZE or BLTSIZH.</summary>
    public Blitter? Blitter { get; set; }

    /// <summary>BZERO of DMACONR: the last blit made only zero words.</summary>
    public bool BlitterZero { get; set; }

    /// <summary>
    /// The DMA control. Kickstart leaves DMAEN (bit 9) on when it starts a program, so a program can switch on its DMA
    /// channels without it. The channels are off.
    /// </summary>
    public ushort Dmacon { get; private set; } = DmaEnable;
    public ushort Intena { get; private set; }
    public ushort Intreq { get; private set; }
    public ushort Adkcon { get; private set; }

    public ushort Read(int offset)
    {
        switch (offset)
        {
            case CustomRegister.Bltddat: return 0;
            // BBUSY (bit 14) is never set, because a blit ends at once.
            case CustomRegister.Dmaconr: return (ushort)((Dmacon & 0x07FF) | (BlitterZero ? 0x2000 : 0));
            case CustomRegister.Vposr:
                // Bit 15 is the long frame flag, bits 14 to 8 are the Agnus ID, and bit 0 is bit 8 of the line.
                return (ushort)((LongFrame ? 0x8000 : 0) | (AgnusId << 8) | (_beam.Line >> 8));
            case CustomRegister.Vhposr: return (ushort)(((_beam.Line & 0xFF) << 8) | _beam.Horizontal);
            case CustomRegister.Joy0dat: return Ports[0].Data;
            case CustomRegister.Joy1dat: return Ports[1].Data;
            case CustomRegister.Adkconr: return Adkcon;
            case CustomRegister.Pot0dat or CustomRegister.Pot1dat: return 0;
            case CustomRegister.Potgor: return PotInputs();
            case CustomRegister.Serdatr: return Serial.ReadData(Intreq);
            case CustomRegister.Dskbytr: return 0;
            case CustomRegister.Intenar: return Intena;
            case CustomRegister.Intreqr: return Intreq;
            // The ECS Denise (8373) has the ID $FC.
            case CustomRegister.Deniseid: return (ushort)(0xFF00 | DeniseId);
            // A read of a strobe register does the same as a write. The value is not defined.
            case CustomRegister.Copjmp1 or CustomRegister.Copjmp2:
                Write(offset, 0);
                return 0;
            default: throw new HardwareAccessException(CustomRegister.Base + (uint)offset);
        }
    }

    public void Write(int offset, ushort value)
    {
        RegisterWritten?.Invoke(offset, value);
        _registers[offset >> 1] = value;
        switch (offset)
        {
            case CustomRegister.Dmacon:
                Dmacon = SetClear(Dmacon, value);
                UpdateAudioDma();
                Blitter?.RunPending();
                DmaChanged?.Invoke();
                break;
            case DiskRegister.Dsklen: DiskLengthWritten?.Invoke(value); break;
            case BlitterRegister.Bltsize: Blitter?.WriteSize(value); break;
            case BlitterRegister.Bltsizh: Blitter?.WriteSizeHorizontal(value); break;
            case CustomRegister.Intena: Intena = SetClear(Intena, value); break;
            case CustomRegister.Intreq: Intreq = SetClear(Intreq, value); break;
            case CustomRegister.Adkcon: Adkcon = SetClear(Adkcon, value); break;
            case CustomRegister.Serper: Serial.Period = value; break;
            case CustomRegister.Joytest:
                Ports[0].Test(value);
                Ports[1].Test(value);
                break;
            case CustomRegister.Serdat:
                Serial.WriteData(value);
                SerialTransmit?.Invoke(value);
                RequestInterrupt(InterruptBit.Tbe);
                break;
            case CustomRegister.Copjmp1: CopperJump?.Invoke(1); break;
            case CustomRegister.Copjmp2: CopperJump?.Invoke(2); break;
        }
    }

    /// <summary>
    /// POTINP: the levels of pins 5 and 9 of the two ports, in bits 8 (DATLX), 10 (DATLY), 12 (DATRX) and 14 (DATRY). A
    /// pin that POTGO makes an output has the value that the program wrote, and an input pin has a pull-up. A pressed
    /// button connects the pin to ground in both cases. The other bits read as 1.
    /// </summary>
    private ushort PotInputs()
    {
        var potgo = this[CustomRegister.Potgo];
        var value = 0xFF00;
        for (var port = 0; port < 2; port++)
        {
            Pin(8 + port * 4, Ports[port].IsPressed(ControllerButton.Middle));
            Pin(10 + port * 4, Ports[port].IsPressed(ControllerButton.Right));
        }

        return (ushort)value;

        void Pin(int dataBit, bool pressed)
        {
            // An output drives the pin weakly, so a pressed button pulls it low. This is how a program reads the right
            // mouse button with POTGO $FF00.
            var output = (potgo & (1 << (dataBit + 1))) != 0;
            var high = (!output || (potgo & (1 << dataBit)) != 0) && !pressed;
            if (!high)
                value &= ~(1 << dataBit);
        }
    }

    /// <summary>Sets an interrupt request, as the hardware does. For example, the start of each frame sets VERTB.</summary>
    public void RequestInterrupt(int bit) => Intreq |= (ushort)(1 << bit);

    /// <summary>Makes the interrupt requests that time causes since the last update.</summary>
    public void Update()
    {
        var frame = _beam.Frame;
        if (frame != _lastFrame)
        {
            // Run the copper of each frame that ended, up to two, so that the copper lists of interlace stay in step
            // with the long frame flag. Only the last frame makes a picture.
            for (var ended = _lastFrame; ended < frame; ended++)
            {
                if (ended >= frame - 2)
                    FrameEnded?.Invoke(LongFrame, ended == frame - 1);
                if (ended != frame - 1)
                    FramesDropped++;
                LongFrame = (this[CustomRegister.Bplcon0] & 0x0004) == 0 || !LongFrame;
                if (ended >= frame - 2)
                    FrameStarted?.Invoke(LongFrame);
            }

            _lastFrame = frame;
            RequestInterrupt(InterruptBit.VerticalBlank);
        }

        var clock = _beam.ColorClocks;
        for (var channel = 0; channel < _audio.Length; channel++)
        {
            ref var timer = ref _audio[channel];
            if (!timer.Running)
                continue;
            // The runtime delivers interrupts only at safe points, so an interrupt can be late. On a real Amiga the
            // CPU takes each interrupt at once, and a program does not lose one. So when more than one interrupt is
            // due, the next request waits until the program clears the last one. The backlog has a limit, so that a
            // long pause of the host does not make a burst of interrupts.
            var interval = AudioInterval(channel);
            var due = (clock - timer.StartClock) / interval + 1;
            timer.Interrupts = Math.Max(timer.Interrupts, due - MaximumAudioBacklog);
            var bit = InterruptBit.Audio0 + channel;
            if (due > timer.Interrupts && (Intreq & (1 << bit)) == 0)
            {
                if (timer.Interrupts > 0)
                    timer.SampleClock += interval;
                timer.Interrupts++;
                RequestInterrupt(bit);
            }
        }

        if (Serial.Update((Intreq & (1 << InterruptBit.Rbf)) != 0))
            RequestInterrupt(InterruptBit.Rbf);
        BeamMoved?.Invoke();
    }

    /// <summary>
    /// The time of the last interrupt request of an audio channel, as the program sees it. A program that samples a
    /// line in the interrupt of the channel sees the line at this time. When the runtime is late, the time does not
    /// go forward for the interrupts that the program does not get.
    /// </summary>
    public TimeSpan AudioSampleTime(int channel) =>
        TimeSpan.FromTicks((long)(_audio[channel].SampleClock * TimeSpan.TicksPerSecond / _beam.ColorClockHz));

    /// <summary>
    /// The color clocks between two interrupts of an audio channel. The channel plays AUDxLEN words, two samples in
    /// each word, and each sample takes AUDxPER color clocks. A length of 0 is 65536 words.
    /// </summary>
    private long AudioInterval(int channel)
    {
        var registers = CustomRegister.Aud0len + channel * CustomRegister.AudioChannelSize;
        var length = this[registers] == 0 ? 65536 : this[registers];
        var period = Math.Max((int)this[registers + 2], 124);
        return 2L * length * period;
    }

    /// <summary>Starts or stops the audio timers when DMACON changes. A channel starts with an interrupt.</summary>
    private void UpdateAudioDma()
    {
        for (var channel = 0; channel < _audio.Length; channel++)
        {
            ref var timer = ref _audio[channel];
            var on = (Dmacon & DmaEnable) != 0 && (Dmacon & (1 << channel)) != 0;
            if (on && !timer.Running)
                timer = new AudioTimer
                {
                    Running = true,
                    StartClock = _beam.ColorClocks,
                    SampleClock = Math.Max(_beam.ColorClocks, timer.SampleClock),
                };
            else if (!on)
                timer.Running = false;
        }
    }

    /// <summary>Bit 15 set: the other bits that are 1 are set. Bit 15 clear: they are cleared.</summary>
    private static ushort SetClear(ushort old, ushort value) =>
        (value & 0x8000) != 0 ? (ushort)(old | (value & 0x7FFF)) : (ushort)(old & ~value);
}
