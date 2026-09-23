using System.Runtime.CompilerServices;

namespace AmigaSharp.Runtime.Cpu;

/// <summary>
/// The programmer-visible state of a 68000: the data registers, the address registers and the condition codes.
/// </summary>
public sealed class CpuState(Memory memory)
{
    public readonly uint[] D = new uint[8];

    /// <summary>A[7] is the stack pointer.</summary>
    public readonly uint[] A = new uint[8];

    // The condition code flags: extend, negative, zero, overflow and carry.
    public bool X, N, Z, V, C;

    public uint Sp
    {
        get => A[7];
        set => A[7] = value;
    }

    /// <summary>The condition code register as a byte: bit 4 is X, bit 3 is N, bit 2 is Z, bit 1 is V, bit 0 is C.</summary>
    public byte Ccr
    {
        get => (byte)((X ? 0x10 : 0) | (N ? 0x08 : 0) | (Z ? 0x04 : 0) | (V ? 0x02 : 0) | (C ? 0x01 : 0));
        set
        {
            X = (value & 0x10) != 0;
            N = (value & 0x08) != 0;
            Z = (value & 0x04) != 0;
            V = (value & 0x02) != 0;
            C = (value & 0x01) != 0;
        }
    }

    // A byte or word write to a data register changes only the low bits.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDataByte(int register, uint value)
    {
        D[register] = (D[register] & 0xFFFF_FF00) | (value & 0xFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDataWord(int register, uint value)
    {
        D[register] = (D[register] & 0xFFFF_0000) | (value & 0xFFFF);
    }

    /// <summary>A word write to an address register sign-extends the value to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAddressWord(int register, uint value)
    {
        A[register] = (uint)(short)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push32(uint value)
    {
        A[7] -= 4;
        memory.Write32(A[7], value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Pop32()
    {
        var value = memory.Read32(A[7]);
        A[7] += 4;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push16(uint value)
    {
        A[7] -= 2;
        memory.Write16(A[7], (ushort)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Pop16()
    {
        var value = memory.Read16(A[7]);
        A[7] += 2;
        return value;
    }

    // MOVE, MOVEQ, TST, CLR and the logical operations set N and Z from the result.
    // They clear V and C, and they do not change X.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLogicFlags8(uint result)
    {
        N = (result & 0x80) != 0;
        Z = (result & 0xFF) == 0;
        V = false;
        C = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLogicFlags16(uint result)
    {
        N = (result & 0x8000) != 0;
        Z = (result & 0xFFFF) == 0;
        V = false;
        C = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLogicFlags32(uint result)
    {
        N = (result & 0x8000_0000) != 0;
        Z = result == 0;
        V = false;
        C = false;
    }
}
