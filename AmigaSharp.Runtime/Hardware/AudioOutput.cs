namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// The sound of the four audio channels of Paula, as 16-bit stereo samples at <see cref="SampleRate"/>. The runtime
/// makes the samples at each safe point, and the host takes them with <see cref="Read"/>.
/// </summary>
/// <remarks>
/// <para>
/// A channel plays with DMA: when DMACON enables it, it loads AUDxLC and AUDxLEN and fetches words from chip memory.
/// Each word has two signed 8-bit samples, and each sample plays for AUDxPER color clocks at the volume AUDxVOL (0 to
/// 64). At the end of the buffer, the channel loads AUDxLC and AUDxLEN again, so a program can give the next buffer in
/// the audio interrupt. Channels 0 and 3 are left, and channels 1 and 2 are right.
/// </para>
/// <para>
/// The low-pass filter of the Amiga is on when the power LED is bright (CIA-A PRA bit 1 is 0). The audio interrupts
/// come from <see cref="CustomChips"/>, not from this class. Modulation (ADKCON) and the play of AUDxDAT without DMA
/// are not emulated.
/// </para>
/// </remarks>
public sealed class AudioOutput(Memory memory, CustomChips custom, Func<bool> filterOn)
{
    public const int SampleRate = 48_000;

    /// <summary>The buffer keeps one second. When it is full, the oldest samples go.</summary>
    private const int Capacity = SampleRate * 2;

    private const int MinimumPeriod = 124;
    private const double ClocksPerSample = Beam.ColorClockHz / SampleRate;

    // A one-pole filter at about 3.3 kHz, near the filter of the A500 and A2000.
    private static readonly double FilterFactor = 1 - Math.Exp(-2 * Math.PI * 3300.0 / SampleRate);

    private readonly Channel[] _channels = [new(), new(), new(), new()];
    private readonly short[] _buffer = new short[Capacity * 2];
    private readonly object _lock = new();
    private int _start;
    private int _count;
    private double _nextSampleClock = -1;
    private double _filterLeft;
    private double _filterRight;

    /// <summary>The number of stereo samples that <see cref="Read"/> can take now.</summary>
    public int Available
    {
        get
        {
            lock (_lock)
                return _count;
        }
    }

    /// <summary>Copies stereo samples (left, right, ...) to the target. Returns the number of stereo samples.</summary>
    public int Read(Span<short> target)
    {
        lock (_lock)
        {
            var samples = Math.Min(_count, target.Length / 2);
            for (var i = 0; i < samples; i++)
            {
                var index = (_start + i) % Capacity * 2;
                target[i * 2] = _buffer[index];
                target[i * 2 + 1] = _buffer[index + 1];
            }

            _start = (_start + samples) % Capacity;
            _count -= samples;
            return samples;
        }
    }

    /// <summary>Makes the samples up to the color clock.</summary>
    public void Update(long now)
    {
        if (_nextSampleClock < 0 || now - _nextSampleClock > Beam.ColorClockHz)
        {
            // The first update, or a long pause of the host: start at the current time.
            _nextSampleClock = now;
            foreach (var channel in _channels)
                channel.NextClock = now;
        }

        UpdateDma(now);
        var filter = filterOn();
        while (_nextSampleClock <= now)
        {
            double left = 0, right = 0;
            for (var number = 0; number < 4; number++)
            {
                var value = Advance(number, _nextSampleClock);
                if (number is 0 or 3)
                    left += value;
                else
                    right += value;
            }

            if (filter)
            {
                _filterLeft += (left - _filterLeft) * FilterFactor;
                _filterRight += (right - _filterRight) * FilterFactor;
                left = _filterLeft;
                right = _filterRight;
            }
            else
            {
                _filterLeft = left;
                _filterRight = right;
            }

            // Two channels at full volume (128 * 64 each) fill the 16-bit range.
            Add((short)Math.Clamp(left * 2, short.MinValue, short.MaxValue),
                (short)Math.Clamp(right * 2, short.MinValue, short.MaxValue));
            _nextSampleClock += ClocksPerSample;
        }
    }

    private void Add(short left, short right)
    {
        lock (_lock)
        {
            if (_count == Capacity)
            {
                _start = (_start + 1) % Capacity;
                _count--;
            }

            var index = (_start + _count) % Capacity * 2;
            _buffer[index] = left;
            _buffer[index + 1] = right;
            _count++;
        }
    }

    /// <summary>Starts the channels that DMACON enabled, and stops the others.</summary>
    private void UpdateDma(long now)
    {
        const ushort dmaEnable = 0x0200;
        for (var number = 0; number < 4; number++)
        {
            var channel = _channels[number];
            var on = (custom.Dmacon & dmaEnable) != 0 && (custom.Dmacon & (1 << number)) != 0;
            if (on && !channel.On)
            {
                channel.On = true;
                channel.NextClock = now;
                channel.Byte = 2;
                channel.WordsLeft = 0;
            }
            else if (!on)
            {
                channel.On = false;
                channel.Sample = 0;
            }
        }
    }

    /// <summary>Plays the samples of the channel up to the clock, and returns its output: the sample times the volume.</summary>
    private double Advance(int number, double clock)
    {
        var channel = _channels[number];
        if (!channel.On)
            return 0;

        var registers = CustomRegister.Aud0lc + number * CustomRegister.AudioChannelSize;
        while (channel.NextClock <= clock)
        {
            if (channel.Byte == 2)
            {
                if (channel.WordsLeft == 0)
                {
                    // The start of a buffer: load the location and the length.
                    channel.Pointer = (uint)(custom[registers] << 16 | custom[registers + 2]) & 0x1F_FFFE;
                    var length = custom[registers + 4];
                    channel.WordsLeft = length == 0 ? 65536 : length;
                }

                channel.Word = memory.Read16(channel.Pointer);
                channel.Pointer += 2;
                channel.WordsLeft--;
                channel.Byte = 0;
            }

            channel.Sample = (sbyte)(channel.Byte == 0 ? channel.Word >> 8 : channel.Word);
            channel.Byte++;
            channel.NextClock += Math.Max((int)custom[registers + 6], MinimumPeriod);
        }

        var volume = Math.Min(custom[registers + 8] & 0x7F, 64);
        return channel.Sample * volume;
    }

    private sealed class Channel
    {
        public bool On;
        public uint Pointer;
        public int WordsLeft;
        public ushort Word;
        public int Byte;
        public sbyte Sample;
        public double NextClock;
    }
}
