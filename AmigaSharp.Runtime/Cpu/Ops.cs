using System.Runtime.CompilerServices;

namespace AmigaSharp.Runtime.Cpu;

/// <summary>
/// The data operations of the 68000 and their effect on the condition codes.
/// The interpreter and the recompiled code both use these methods.
/// </summary>
/// <remarks>
/// Each method takes the operands as unsigned values. It uses only the bits of the operand size, and it returns the
/// result masked to the operand size. The caller writes the result to the destination.
/// </remarks>
public static class Ops
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Add(CpuState cpu, Size size, uint source, uint destination)
    {
        var mask = size.Mask();
        var msb = size.Msb();
        source &= mask;
        destination &= mask;
        var result = (source + destination) & mask;
        cpu.C = cpu.X = (((source & destination) | (~result & (source | destination))) & msb) != 0;
        cpu.V = ((source ^ result) & (destination ^ result) & msb) != 0;
        cpu.N = (result & msb) != 0;
        cpu.Z = result == 0;
        return result;
    }

    /// <summary>ADDX. Z is cleared if the result is not zero, and it does not change otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Addx(CpuState cpu, Size size, uint source, uint destination)
    {
        var mask = size.Mask();
        var msb = size.Msb();
        source &= mask;
        destination &= mask;
        var result = (source + destination + (cpu.X ? 1u : 0u)) & mask;
        cpu.C = cpu.X = (((source & destination) | (~result & (source | destination))) & msb) != 0;
        cpu.V = ((source ^ result) & (destination ^ result) & msb) != 0;
        cpu.N = (result & msb) != 0;
        if (result != 0)
            cpu.Z = false;
        return result;
    }

    /// <summary>Computes destination minus source.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Sub(CpuState cpu, Size size, uint source, uint destination)
    {
        var result = Compare(cpu, size, source, destination);
        cpu.X = cpu.C;
        return result;
    }

    /// <summary>SUBX. Z is cleared if the result is not zero, and it does not change otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Subx(CpuState cpu, Size size, uint source, uint destination)
    {
        var mask = size.Mask();
        var msb = size.Msb();
        source &= mask;
        destination &= mask;
        var result = (destination - source - (cpu.X ? 1u : 0u)) & mask;
        cpu.C = cpu.X = (((source & ~destination) | (result & ~destination) | (source & result)) & msb) != 0;
        cpu.V = ((source ^ destination) & (result ^ destination) & msb) != 0;
        cpu.N = (result & msb) != 0;
        if (result != 0)
            cpu.Z = false;
        return result;
    }

    /// <summary>CMP: sets the flags of destination minus source. X does not change.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compare(CpuState cpu, Size size, uint source, uint destination)
    {
        var mask = size.Mask();
        var msb = size.Msb();
        source &= mask;
        destination &= mask;
        var result = (destination - source) & mask;
        cpu.C = (((source & ~destination) | (result & ~destination) | (source & result)) & msb) != 0;
        cpu.V = ((source ^ destination) & (result ^ destination) & msb) != 0;
        cpu.N = (result & msb) != 0;
        cpu.Z = result == 0;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Neg(CpuState cpu, Size size, uint value) => Sub(cpu, size, value, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Negx(CpuState cpu, Size size, uint value) => Subx(cpu, size, value, 0);

    /// <summary>Sets N and Z from the result and clears V and C. MOVE, TST, CLR and the logical operations use this.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Logic(CpuState cpu, Size size, uint result)
    {
        result &= size.Mask();
        cpu.N = (result & size.Msb()) != 0;
        cpu.Z = result == 0;
        cpu.V = false;
        cpu.C = false;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Mulu(CpuState cpu, uint source, uint destination)
    {
        return Logic(cpu, Size.Long, (source & 0xFFFF) * (destination & 0xFFFF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Muls(CpuState cpu, uint source, uint destination)
    {
        return Logic(cpu, Size.Long, (uint)((short)source * (short)destination));
    }

    /// <summary>
    /// DIVU.W: divides the 32-bit destination by the 16-bit source. Returns the new value of the data register:
    /// the remainder in the high word and the quotient in the low word. On overflow, the register does not change.
    /// </summary>
    /// <exception cref="CpuTrapException">The divisor is zero.</exception>
    public static uint Divu(CpuState cpu, uint source, uint destination)
    {
        var divisor = source & 0xFFFF;
        cpu.C = false;
        if (divisor == 0)
        {
            cpu.N = false;
            cpu.Z = false;
            cpu.V = false;
            throw new CpuTrapException(ExceptionVector.ZeroDivide);
        }

        var quotient = destination / divisor;
        if (quotient > 0xFFFF)
        {
            // On overflow, N and Z do not change.
            cpu.V = true;
            return destination;
        }

        var remainder = destination % divisor;
        cpu.V = false;
        cpu.N = (quotient & 0x8000) != 0;
        cpu.Z = quotient == 0;
        return remainder << 16 | quotient;
    }

    /// <summary>DIVS.W: the signed form of <see cref="Divu"/>. The remainder has the sign of the dividend.</summary>
    /// <exception cref="CpuTrapException">The divisor is zero.</exception>
    public static uint Divs(CpuState cpu, uint source, uint destination)
    {
        var divisor = (int)(short)source;
        cpu.C = false;
        if (divisor == 0)
        {
            cpu.N = false;
            cpu.Z = false;
            cpu.V = false;
            throw new CpuTrapException(ExceptionVector.ZeroDivide);
        }

        var dividend = (long)(int)destination;
        var quotient = dividend / divisor;
        if (quotient is > short.MaxValue or < short.MinValue)
        {
            // On overflow, N and Z do not change.
            cpu.V = true;
            return destination;
        }

        var remainder = dividend % divisor;
        cpu.V = false;
        cpu.N = quotient < 0;
        cpu.Z = quotient == 0;
        return (uint)(remainder & 0xFFFF) << 16 | (uint)(quotient & 0xFFFF);
    }

    /// <summary>CHK.W: traps if the data register is less than zero or greater than the bound.</summary>
    /// <exception cref="CpuTrapException">The value is out of bounds.</exception>
    public static void Chk(CpuState cpu, uint bound, uint value)
    {
        var upper = (short)bound;
        var register = (short)value;
        cpu.Z = register == 0;
        cpu.V = false;
        cpu.C = false;
        if (register < 0)
        {
            cpu.N = true;
            throw new CpuTrapException(ExceptionVector.Chk);
        }

        if (register > upper)
        {
            cpu.N = false;
            throw new CpuTrapException(ExceptionVector.Chk);
        }
    }

    // The shifts and the rotates. The count is 0 to 63. A count of 0 sets N and Z, clears V, and sets C as below.

    public static uint Asl(CpuState cpu, Size size, uint value, int count)
    {
        var bits = size.Bits();
        var mask = size.Mask();
        value &= mask;
        if (count == 0)
            return ShiftResult(cpu, size, value, carry: false, setX: false);

        uint result;
        bool carry;
        bool overflow;
        if (count < bits)
        {
            result = (value << count) & mask;
            carry = ((value >> (bits - count)) & 1) != 0;
            // V is set if the sign bit changes at any time during the shift: the top count+1 bits are not all equal.
            var top = count + 1 >= bits ? mask : mask & ~((1u << (bits - count - 1)) - 1);
            var topBits = value & top;
            overflow = topBits != 0 && topBits != top;
        }
        else
        {
            result = 0;
            carry = count == bits && (value & 1) != 0;
            overflow = value != 0;
        }

        ShiftResult(cpu, size, result, carry, setX: true);
        cpu.V = overflow;
        return result;
    }

    public static uint Asr(CpuState cpu, Size size, uint value, int count)
    {
        var bits = size.Bits();
        var mask = size.Mask();
        value &= mask;
        if (count == 0)
            return ShiftResult(cpu, size, value, carry: false, setX: false);

        var signed = (int)size.SignExtend(value);
        uint result;
        bool carry;
        if (count < bits)
        {
            result = (uint)(signed >> count) & mask;
            carry = ((signed >> (count - 1)) & 1) != 0;
        }
        else
        {
            // C is bit (count - 1) of the operand. For a count greater than the size, that bit is zero.
            result = signed < 0 ? mask : 0;
            carry = count == bits && signed < 0;
        }

        return ShiftResult(cpu, size, result, carry, setX: true);
    }

    public static uint Lsl(CpuState cpu, Size size, uint value, int count)
    {
        var bits = size.Bits();
        var mask = size.Mask();
        value &= mask;
        if (count == 0)
            return ShiftResult(cpu, size, value, carry: false, setX: false);

        uint result;
        bool carry;
        if (count < bits)
        {
            result = (value << count) & mask;
            carry = ((value >> (bits - count)) & 1) != 0;
        }
        else
        {
            result = 0;
            carry = count == bits && (value & 1) != 0;
        }

        return ShiftResult(cpu, size, result, carry, setX: true);
    }

    public static uint Lsr(CpuState cpu, Size size, uint value, int count)
    {
        var bits = size.Bits();
        var mask = size.Mask();
        value &= mask;
        if (count == 0)
            return ShiftResult(cpu, size, value, carry: false, setX: false);

        uint result;
        bool carry;
        if (count < bits)
        {
            result = value >> count;
            carry = ((value >> (count - 1)) & 1) != 0;
        }
        else
        {
            result = 0;
            carry = count == bits && (value & size.Msb()) != 0;
        }

        return ShiftResult(cpu, size, result, carry, setX: true);
    }

    public static uint Rol(CpuState cpu, Size size, uint value, int count)
    {
        var bits = size.Bits();
        var mask = size.Mask();
        value &= mask;
        if (count == 0)
            return ShiftResult(cpu, size, value, carry: false, setX: false);

        var n = count % bits;
        var result = n == 0 ? value : ((value << n) | (value >> (bits - n))) & mask;
        return ShiftResult(cpu, size, result, carry: (result & 1) != 0, setX: false);
    }

    public static uint Ror(CpuState cpu, Size size, uint value, int count)
    {
        var bits = size.Bits();
        var mask = size.Mask();
        value &= mask;
        if (count == 0)
            return ShiftResult(cpu, size, value, carry: false, setX: false);

        var n = count % bits;
        var result = n == 0 ? value : ((value >> n) | (value << (bits - n))) & mask;
        return ShiftResult(cpu, size, result, carry: (result & size.Msb()) != 0, setX: false);
    }

    /// <summary>ROXL: rotates through X. A count of 0 copies X to C.</summary>
    public static uint Roxl(CpuState cpu, Size size, uint value, int count)
    {
        var mask = size.Mask();
        var msb = size.Msb();
        value &= mask;
        var n = count % (size.Bits() + 1);
        for (var i = 0; i < n; i++)
        {
            var bitOut = (value & msb) != 0;
            value = ((value << 1) | (cpu.X ? 1u : 0u)) & mask;
            cpu.X = bitOut;
        }

        return ShiftResult(cpu, size, value, carry: cpu.X, setX: false);
    }

    /// <summary>ROXR: rotates through X. A count of 0 copies X to C.</summary>
    public static uint Roxr(CpuState cpu, Size size, uint value, int count)
    {
        var mask = size.Mask();
        var msb = size.Msb();
        value &= mask;
        var n = count % (size.Bits() + 1);
        for (var i = 0; i < n; i++)
        {
            var bitOut = (value & 1) != 0;
            value = (value >> 1) | (cpu.X ? msb : 0);
            cpu.X = bitOut;
        }

        return ShiftResult(cpu, size, value, carry: cpu.X, setX: false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ShiftResult(CpuState cpu, Size size, uint result, bool carry, bool setX)
    {
        cpu.C = carry;
        if (setX)
            cpu.X = carry;
        cpu.V = false;
        cpu.N = (result & size.Msb()) != 0;
        cpu.Z = result == 0;
        return result;
    }

    // BCD arithmetic. These formulas also give the undocumented N and V flags of a real 68000.

    public static uint Abcd(CpuState cpu, uint source, uint destination)
    {
        source &= 0xFF;
        destination &= 0xFF;
        var sum = source + destination + (cpu.X ? 1u : 0u);
        var carries = ((source & destination) | (~sum & source) | (~sum & destination)) & 0x88;
        var decimalCarries = (((sum + 0x66) ^ sum) & 0x110) >> 1;
        var correction = (carries | decimalCarries) - ((carries | decimalCarries) >> 2);
        var result = sum + correction;
        cpu.C = cpu.X = (((carries | (sum & ~result)) >> 7) & 1) != 0;
        cpu.V = (((~sum & result) >> 7) & 1) != 0;
        return BcdResult(cpu, result);
    }

    /// <summary>Computes destination minus source in BCD.</summary>
    public static uint Sbcd(CpuState cpu, uint source, uint destination)
    {
        source &= 0xFF;
        destination &= 0xFF;
        var difference = destination - source - (cpu.X ? 1u : 0u);
        var borrows = ((~destination & source) | (difference & ~destination) | (difference & source)) & 0x88;
        var correction = borrows - (borrows >> 2);
        var result = difference - correction;
        cpu.C = cpu.X = (((borrows | (~difference & result)) >> 7) & 1) != 0;
        cpu.V = (((difference & ~result) >> 7) & 1) != 0;
        return BcdResult(cpu, result);
    }

    public static uint Nbcd(CpuState cpu, uint value) => Sbcd(cpu, value, 0);

    private static uint BcdResult(CpuState cpu, uint result)
    {
        result &= 0xFF;
        cpu.N = (result & 0x80) != 0;
        if (result != 0)
            cpu.Z = false;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TestCondition(CpuState cpu, Condition condition)
    {
        return condition switch
        {
            Condition.True => true,
            Condition.False => false,
            Condition.Hi => !cpu.C && !cpu.Z,
            Condition.Ls => cpu.C || cpu.Z,
            Condition.Cc => !cpu.C,
            Condition.Cs => cpu.C,
            Condition.Ne => !cpu.Z,
            Condition.Eq => cpu.Z,
            Condition.Vc => !cpu.V,
            Condition.Vs => cpu.V,
            Condition.Pl => !cpu.N,
            Condition.Mi => cpu.N,
            Condition.Ge => cpu.N == cpu.V,
            Condition.Lt => cpu.N != cpu.V,
            Condition.Gt => !cpu.Z && cpu.N == cpu.V,
            _ => cpu.Z || cpu.N != cpu.V,
        };
    }
}
