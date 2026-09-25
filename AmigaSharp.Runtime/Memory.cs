using System.Runtime.CompilerServices;
using System.Text;

namespace AmigaSharp.Runtime;

/// <summary>
/// The 24-bit address space of a 68000 Amiga. All word and long values are big-endian.
/// </summary>
public sealed class Memory
{
    /// <summary>The 68000 has 24 address lines. The CPU ignores the top 8 bits of an address.</summary>
    public const uint AddressMask = 0x00FF_FFFF;

    public const int Size = 0x0100_0000;

    private const int PageShift = 16;

    private readonly byte[] _data = new byte[Size];
    private readonly bool[] _hardwarePages = new bool[Size >> PageShift];

    /// <param name="guardHardware">
    /// If true, the custom chips and the CIAs use their addresses. An access there goes to <see cref="Hardware"/>, or
    /// throws <see cref="HardwareAccessException"/> if no hardware is connected.
    /// If false, the whole address space is RAM. The CPU tests use this.
    /// </param>
    public Memory(bool guardHardware = true)
    {
        if (!guardHardware)
            return;

        // The CIA chips use $BF0000 to $BFFFFF.
        MarkHardware(0xBF0000, 0xC00000);
        // The real-time clock and the custom chips use $DC0000 to $DFFFFF.
        MarkHardware(0xDC0000, 0xE00000);
    }

    /// <summary>The custom chips and the CIAs. Null means that an access to them throws.</summary>
    public IHardware? Hardware { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(uint address)
    {
        address &= AddressMask;
        if (_hardwarePages[address >> PageShift])
            return HardwareOrThrow(address).Read8(address);
        return _data[address];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Read16(uint address)
    {
        address = CheckAligned(address);
        if (_hardwarePages[address >> PageShift])
            return HardwareOrThrow(address).Read16(address);
        return (ushort)(_data[address] << 8 | _data[(address + 1) & AddressMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(uint address)
    {
        return (uint)Read16(address) << 16 | Read16(address + 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write8(uint address, byte value)
    {
        address &= AddressMask;
        if (_hardwarePages[address >> PageShift])
        {
            HardwareOrThrow(address).Write8(address, value);
            return;
        }

        _data[address] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write16(uint address, ushort value)
    {
        address = CheckAligned(address);
        if (_hardwarePages[address >> PageShift])
        {
            HardwareOrThrow(address).Write16(address, value);
            return;
        }

        _data[address] = (byte)(value >> 8);
        _data[(address + 1) & AddressMask] = (byte)value;
    }

    /// <summary>
    /// The RAM at the address, for fast access by the runtime, for example to draw into a bitplane. The range must not
    /// include hardware addresses.
    /// </summary>
    public Span<byte> Ram(uint address, int length)
    {
        address &= AddressMask;
        if (_hardwarePages[address >> PageShift] || _hardwarePages[(address + (uint)Math.Max(length - 1, 0)) >> PageShift])
            throw new HardwareAccessException(address);
        return _data.AsSpan((int)address, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write32(uint address, uint value)
    {
        Write16(address, (ushort)(value >> 16));
        Write16(address + 2, (ushort)value);
    }

    public void WriteBytes(uint address, ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            Write8(address + (uint)i, bytes[i]);
    }

    public byte[] ReadBytes(uint address, int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
            bytes[i] = Read8(address + (uint)i);
        return bytes;
    }

    /// <summary>Reads the bytes from the address up to the first zero byte. The zero byte is not included.</summary>
    public byte[] ReadCStringBytes(uint address)
    {
        var bytes = new List<byte>();
        for (var value = Read8(address); value != 0; value = Read8(++address))
            bytes.Add(value);
        return bytes.ToArray();
    }

    /// <summary>Reads a zero-terminated string. Amiga text is ISO-8859-1.</summary>
    public string ReadCString(uint address)
    {
        return Encoding.Latin1.GetString(ReadCStringBytes(address));
    }

    private void MarkHardware(uint start, uint end)
    {
        for (var page = start >> PageShift; page < end >> PageShift; page++)
            _hardwarePages[page] = true;
    }

    private IHardware HardwareOrThrow(uint address) => Hardware ?? throw new HardwareAccessException(address);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CheckAligned(uint address)
    {
        // The 68000 cannot access a word or a long at an odd address.
        if ((address & 1) != 0)
            throw new AddressErrorException(address & AddressMask);
        return address & AddressMask;
    }
}
