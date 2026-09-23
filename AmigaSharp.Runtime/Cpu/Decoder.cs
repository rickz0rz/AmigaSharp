namespace AmigaSharp.Runtime.Cpu;

/// <summary>
/// Decodes 68000 machine code. The interpreter and the translator both use it.
/// </summary>
public static class Decoder
{
    // The groups of effective addressing modes that an instruction can accept.
    [Flags]
    private enum Ea
    {
        None = 0,
        Dn = 1 << 0,
        An = 1 << 1,
        Ind = 1 << 2,
        PostInc = 1 << 3,
        PreDec = 1 << 4,
        Disp = 1 << 5,
        Index = 1 << 6,
        AbsW = 1 << 7,
        AbsL = 1 << 8,
        PcDisp = 1 << 9,
        PcIndex = 1 << 10,
        Imm = 1 << 11,

        All = (1 << 12) - 1,
        Data = All & ~An,
        Memory = Data & ~Dn,
        Control = Ind | Disp | Index | AbsW | AbsL | PcDisp | PcIndex,
        Alterable = All & ~(PcDisp | PcIndex | Imm),
        DataAlterable = Data & Alterable,
        MemoryAlterable = Memory & Alterable,
    }

    private static readonly Size[] StandardSizes = [Size.Byte, Size.Word, Size.Long];

    /// <summary>
    /// Decodes the instruction at the address. The word reader returns the big-endian word at an even address.
    /// An opcode that the 68000 does not implement decodes as <see cref="Operation.Illegal"/>,
    /// <see cref="Operation.LineA"/> or <see cref="Operation.LineF"/>.
    /// </summary>
    public static Instruction Decode(uint address, Func<uint, ushort> readWord)
    {
        var reader = new Reader(address, readWord);
        var opcode = reader.Next();
        var instruction = DecodeOpcode(opcode, ref reader);
        if (instruction == null)
            return new Instruction { Address = address, Opcode = opcode, Operation = Operation.Illegal, Length = 2 };
        return instruction with { Address = address, Opcode = opcode, Length = (int)(reader.Position - address) };
    }

    private static Instruction? DecodeOpcode(ushort op, ref Reader r)
    {
        return (op >> 12) switch
        {
            0x0 => DecodeLine0(op, ref r),
            0x1 => DecodeMove(op, Size.Byte, ref r),
            0x2 => DecodeMove(op, Size.Long, ref r),
            0x3 => DecodeMove(op, Size.Word, ref r),
            0x4 => DecodeLine4(op, ref r),
            0x5 => DecodeLine5(op, ref r),
            0x6 => DecodeBranch(op, ref r),
            0x7 => (op & 0x0100) == 0
                ? Make(Operation.Moveq, Size.Long, Operand.Imm((uint)(sbyte)op), Operand.Dn(RegX(op)))
                : null,
            0x8 => DecodeLine8(op, ref r),
            0x9 => DecodeAddSub(op, Operation.Sub, Operation.Suba, Operation.Subx, ref r),
            0xA => Make(Operation.LineA),
            0xB => DecodeLineB(op, ref r),
            0xC => DecodeLineC(op, ref r),
            0xD => DecodeAddSub(op, Operation.Add, Operation.Adda, Operation.Addx, ref r),
            0xE => DecodeShift(op, ref r),
            _ => Make(Operation.LineF),
        };
    }

    private static Instruction? DecodeLine0(ushort op, ref Reader r)
    {
        // MOVEP
        if ((op & 0x0138) == 0x0108)
        {
            var opmode = (op >> 6) & 7;
            var size = (opmode & 1) == 0 ? Size.Word : Size.Long;
            var memory = new Operand(AddressingMode.Displacement, op & 7, (short)r.Next());
            var dn = Operand.Dn(RegX(op));
            return opmode >= 6
                ? Make(Operation.Movep, size, dn, memory)
                : Make(Operation.Movep, size, memory, dn);
        }

        // Bit operations with the bit number in a data register.
        if ((op & 0x0100) != 0)
        {
            var bitOperation = BitOperation(op);
            var allowed = bitOperation == Operation.Btst ? Ea.Data : Ea.DataAlterable;
            var size = EaMode(op) == 0 ? Size.Long : Size.Byte;
            var destination = ReadEa(op, size, allowed, ref r);
            return destination == null ? null : Make(bitOperation, size, Operand.Dn(RegX(op)), destination.Value);
        }

        var kind = (op >> 9) & 7;
        if (kind == 4)
        {
            // Bit operations with an immediate bit number.
            var bitOperation = BitOperation(op);
            var bitNumber = Operand.Imm((uint)(r.Next() & 0xFF));
            var allowed = bitOperation == Operation.Btst ? Ea.Data & ~Ea.Imm : Ea.DataAlterable;
            var size = EaMode(op) == 0 ? Size.Long : Size.Byte;
            var destination = ReadEa(op, size, allowed, ref r);
            return destination == null ? null : Make(bitOperation, size, bitNumber, destination.Value);
        }

        Operation operation;
        switch (kind)
        {
            case 0: operation = Operation.Ori; break;
            case 1: operation = Operation.Andi; break;
            case 2: operation = Operation.Subi; break;
            case 3: operation = Operation.Addi; break;
            case 5: operation = Operation.Eori; break;
            case 6: operation = Operation.Cmpi; break;
            default: return null;
        }

        // ORI, ANDI and EORI to CCR or SR.
        if (operation is Operation.Ori or Operation.Andi or Operation.Eori)
        {
            if ((op & 0xFF) == 0x3C)
                return Make(operation, Size.Byte, Operand.Imm((uint)(r.Next() & 0xFF)), new Operand(AddressingMode.Ccr));
            if ((op & 0xFF) == 0x7C)
                return Make(operation, Size.Word, Operand.Imm(r.Next()), new Operand(AddressingMode.Sr));
        }

        if (!TryStandardSize(op, out var operandSize))
            return null;
        var immediate = Operand.Imm(r.ReadImmediate(operandSize));
        var target = ReadEa(op, operandSize, Ea.DataAlterable, ref r);
        return target == null ? null : Make(operation, operandSize, immediate, target.Value);
    }

    private static Instruction? DecodeMove(ushort op, Size size, ref Reader r)
    {
        var destinationMode = (op >> 6) & 7;
        var source = ReadEa(EaMode(op), op & 7, size, size == Size.Byte ? Ea.Data : Ea.All, ref r);
        if (source == null)
            return null;

        if (destinationMode == 1)
            return size == Size.Byte ? null : Make(Operation.Movea, size, source.Value, Operand.An(RegX(op)));

        var destination = ReadEa(destinationMode, RegX(op), size, Ea.DataAlterable, ref r);
        return destination == null ? null : Make(Operation.Move, size, source.Value, destination.Value);
    }

    private static Instruction? DecodeLine4(ushort op, ref Reader r)
    {
        switch (op)
        {
            case 0x4AFC: return Make(Operation.Illegal);
            case 0x4E70: return Make(Operation.Reset);
            case 0x4E71: return Make(Operation.Nop);
            case 0x4E72: return Make(Operation.Stop, Size.Word, Operand.Imm(r.Next()));
            case 0x4E73: return Make(Operation.Rte);
            case 0x4E75: return Make(Operation.Rts);
            case 0x4E76: return Make(Operation.Trapv);
            case 0x4E77: return Make(Operation.Rtr);
        }

        switch (op & 0xFFF0)
        {
            case 0x4E40:
                return Make(Operation.Trap, Size.Word, Operand.Imm((uint)(op & 0xF)));
            case 0x4E50:
                return (op & 8) == 0
                    ? Make(Operation.Link, Size.Word, Operand.Imm((uint)(short)r.Next()), Operand.An(op & 7))
                    : Make(Operation.Unlk, Size.Long, Operand.None, Operand.An(op & 7));
            case 0x4E60:
                var usp = new Operand(AddressingMode.Usp);
                return (op & 8) == 0
                    ? Make(Operation.Move, Size.Long, Operand.An(op & 7), usp)
                    : Make(Operation.Move, Size.Long, usp, Operand.An(op & 7));
        }

        switch (op & 0xFFC0)
        {
            case 0x4E80: return MakeSingle(Operation.Jsr, Size.Long, op, Ea.Control, ref r, asSource: true);
            case 0x4EC0: return MakeSingle(Operation.Jmp, Size.Long, op, Ea.Control, ref r, asSource: true);
            case 0x40C0:
                var sr = ReadEa(op, Size.Word, Ea.DataAlterable, ref r);
                return sr == null ? null : Make(Operation.Move, Size.Word, new Operand(AddressingMode.Sr), sr.Value);
            case 0x44C0:
                var toCcr = ReadEa(op, Size.Word, Ea.Data, ref r);
                return toCcr == null ? null : Make(Operation.Move, Size.Word, toCcr.Value, new Operand(AddressingMode.Ccr));
            case 0x46C0:
                var toSr = ReadEa(op, Size.Word, Ea.Data, ref r);
                return toSr == null ? null : Make(Operation.Move, Size.Word, toSr.Value, new Operand(AddressingMode.Sr));
            case 0x4800: return MakeSingle(Operation.Nbcd, Size.Byte, op, Ea.DataAlterable, ref r);
            case 0x4AC0: return MakeSingle(Operation.Tas, Size.Byte, op, Ea.DataAlterable, ref r);
        }

        if ((op & 0xF1C0) == 0x41C0)
        {
            var address = ReadEa(op, Size.Long, Ea.Control, ref r);
            return address == null ? null : Make(Operation.Lea, Size.Long, address.Value, Operand.An(RegX(op)));
        }

        if ((op & 0xF1C0) == 0x4180)
        {
            var bound = ReadEa(op, Size.Word, Ea.Data, ref r);
            return bound == null ? null : Make(Operation.Chk, Size.Word, bound.Value, Operand.Dn(RegX(op)));
        }

        switch (op & 0xFFF8)
        {
            case 0x4840: return Make(Operation.Swap, Size.Long, Operand.None, Operand.Dn(op & 7));
            case 0x4880: return Make(Operation.Ext, Size.Word, Operand.None, Operand.Dn(op & 7));
            case 0x48C0: return Make(Operation.Ext, Size.Long, Operand.None, Operand.Dn(op & 7));
        }

        if ((op & 0xFFC0) == 0x4840)
            return MakeSingle(Operation.Pea, Size.Long, op, Ea.Control, ref r, asSource: true);

        if ((op & 0xFB80) == 0x4880)
            return DecodeMovem(op, ref r);

        var single = (op & 0xFF00) switch
        {
            0x4000 => Operation.Negx,
            0x4200 => Operation.Clr,
            0x4400 => Operation.Neg,
            0x4600 => Operation.Not,
            0x4A00 => Operation.Tst,
            _ => Operation.Illegal,
        };
        if (single != Operation.Illegal && TryStandardSize(op, out var size))
            return MakeSingle(single, size, op, Ea.DataAlterable, ref r);

        return null;
    }

    private static Instruction? DecodeMovem(ushort op, ref Reader r)
    {
        var size = (op & 0x40) == 0 ? Size.Word : Size.Long;
        var registersToMemory = (op & 0x0400) == 0;
        var mask = r.Next();
        var allowed = registersToMemory
            ? Ea.Ind | Ea.PreDec | Ea.Disp | Ea.Index | Ea.AbsW | Ea.AbsL
            : Ea.Ind | Ea.PostInc | Ea.Disp | Ea.Index | Ea.AbsW | Ea.AbsL | Ea.PcDisp | Ea.PcIndex;
        var memory = ReadEa(op, size, allowed, ref r);
        if (memory == null)
            return null;

        // The predecrement form stores the mask in reverse order: bit 0 is A7 and bit 15 is D0.
        if (memory.Value.Mode == AddressingMode.PreDecrement)
            mask = ReverseBits(mask);

        return new Instruction
        {
            Address = 0, Opcode = 0, Length = 0,
            Operation = Operation.Movem,
            Size = size,
            Source = registersToMemory ? Operand.None : memory.Value,
            Destination = registersToMemory ? memory.Value : Operand.None,
            RegisterMask = mask,
            RegistersToMemory = registersToMemory,
        };
    }

    private static Instruction? DecodeLine5(ushort op, ref Reader r)
    {
        if (((op >> 6) & 3) == 3)
        {
            var condition = (Condition)((op >> 8) & 0xF);
            if (EaMode(op) == 1)
            {
                var start = r.Position;
                var displacement = (short)r.Next();
                return new Instruction
                {
                    Address = 0, Opcode = 0, Length = 0,
                    Operation = Operation.Dbcc,
                    Condition = condition,
                    Destination = Operand.Dn(op & 7),
                    Target = start + (uint)displacement,
                };
            }

            var destination = ReadEa(op, Size.Byte, Ea.DataAlterable, ref r);
            return destination == null
                ? null
                : Make(Operation.Scc, Size.Byte, Operand.None, destination.Value) with { Condition = condition };
        }

        TryStandardSize(op, out var size);
        var data = (uint)RegX(op);
        if (data == 0)
            data = 8;
        var target = ReadEa(op, size, size == Size.Byte ? Ea.DataAlterable : Ea.Alterable, ref r);
        var operation = (op & 0x0100) == 0 ? Operation.Addq : Operation.Subq;
        return target == null ? null : Make(operation, size, Operand.Imm(data), target.Value);
    }

    private static Instruction DecodeBranch(ushort op, ref Reader r)
    {
        var start = r.Position;
        int displacement = (sbyte)op;
        if (displacement == 0)
            displacement = (short)r.Next();

        var condition = (Condition)((op >> 8) & 0xF);
        return new Instruction
        {
            Address = 0, Opcode = 0, Length = 0,
            // Condition code 1 (false) is BSR.
            Operation = condition == Condition.False ? Operation.Bsr : Operation.Bcc,
            Condition = condition,
            Target = start + (uint)displacement,
        };
    }

    private static Instruction? DecodeLine8(ushort op, ref Reader r)
    {
        if ((op & 0x01C0) == 0x00C0)
            return DecodeWordToDataRegister(Operation.Divu, op, ref r);
        if ((op & 0x01C0) == 0x01C0)
            return DecodeWordToDataRegister(Operation.Divs, op, ref r);
        if ((op & 0x01F0) == 0x0100)
            return DecodeExtended(Operation.Sbcd, Size.Byte, op);
        return DecodeLogical(Operation.Or, op, ref r);
    }

    private static Instruction? DecodeLineB(ushort op, ref Reader r)
    {
        var opmode = (op >> 6) & 7;
        if (opmode is 3 or 7)
        {
            var size = opmode == 3 ? Size.Word : Size.Long;
            var source = ReadEa(op, size, Ea.All, ref r);
            return source == null ? null : Make(Operation.Cmpa, size, source.Value, Operand.An(RegX(op)));
        }

        TryStandardSize(op, out var operandSize);
        if ((op & 0x0100) == 0)
        {
            var source = ReadEa(op, operandSize, operandSize == Size.Byte ? Ea.Data : Ea.All, ref r);
            return source == null ? null : Make(Operation.Cmp, operandSize, source.Value, Operand.Dn(RegX(op)));
        }

        if (EaMode(op) == 1)
        {
            return Make(Operation.Cmpm, operandSize,
                new Operand(AddressingMode.PostIncrement, op & 7),
                new Operand(AddressingMode.PostIncrement, RegX(op)));
        }

        var destination = ReadEa(op, operandSize, Ea.DataAlterable, ref r);
        return destination == null ? null : Make(Operation.Eor, operandSize, Operand.Dn(RegX(op)), destination.Value);
    }

    private static Instruction? DecodeLineC(ushort op, ref Reader r)
    {
        if ((op & 0x01C0) == 0x00C0)
            return DecodeWordToDataRegister(Operation.Mulu, op, ref r);
        if ((op & 0x01C0) == 0x01C0)
            return DecodeWordToDataRegister(Operation.Muls, op, ref r);
        if ((op & 0x01F0) == 0x0100)
            return DecodeExtended(Operation.Abcd, Size.Byte, op);

        switch (op & 0x01F8)
        {
            case 0x0140: return Make(Operation.Exg, Size.Long, Operand.Dn(RegX(op)), Operand.Dn(op & 7));
            case 0x0148: return Make(Operation.Exg, Size.Long, Operand.An(RegX(op)), Operand.An(op & 7));
            case 0x0188: return Make(Operation.Exg, Size.Long, Operand.Dn(RegX(op)), Operand.An(op & 7));
        }

        return DecodeLogical(Operation.And, op, ref r);
    }

    private static Instruction? DecodeAddSub(ushort op, Operation operation, Operation addressOperation,
        Operation extendedOperation, ref Reader r)
    {
        var opmode = (op >> 6) & 7;
        if (opmode is 3 or 7)
        {
            var size = opmode == 3 ? Size.Word : Size.Long;
            var source = ReadEa(op, size, Ea.All, ref r);
            return source == null ? null : Make(addressOperation, size, source.Value, Operand.An(RegX(op)));
        }

        TryStandardSize(op, out var operandSize);
        if ((op & 0x0130) == 0x0100)
            return DecodeExtended(extendedOperation, operandSize, op);

        if ((op & 0x0100) == 0)
        {
            var source = ReadEa(op, operandSize, operandSize == Size.Byte ? Ea.Data : Ea.All, ref r);
            return source == null ? null : Make(operation, operandSize, source.Value, Operand.Dn(RegX(op)));
        }

        var destination = ReadEa(op, operandSize, Ea.MemoryAlterable, ref r);
        return destination == null ? null : Make(operation, operandSize, Operand.Dn(RegX(op)), destination.Value);
    }

    private static Instruction? DecodeLogical(Operation operation, ushort op, ref Reader r)
    {
        TryStandardSize(op, out var size);
        if ((op & 0x0100) == 0)
        {
            var source = ReadEa(op, size, Ea.Data, ref r);
            return source == null ? null : Make(operation, size, source.Value, Operand.Dn(RegX(op)));
        }

        var destination = ReadEa(op, size, Ea.MemoryAlterable, ref r);
        return destination == null ? null : Make(operation, size, Operand.Dn(RegX(op)), destination.Value);
    }

    private static Instruction? DecodeWordToDataRegister(Operation operation, ushort op, ref Reader r)
    {
        var source = ReadEa(op, Size.Word, Ea.Data, ref r);
        return source == null ? null : Make(operation, Size.Word, source.Value, Operand.Dn(RegX(op)));
    }

    /// <summary>ABCD, SBCD, ADDX and SUBX: Dy,Dx or -(Ay),-(Ax).</summary>
    private static Instruction DecodeExtended(Operation operation, Size size, ushort op)
    {
        return (op & 8) == 0
            ? Make(operation, size, Operand.Dn(op & 7), Operand.Dn(RegX(op)))
            : Make(operation, size,
                new Operand(AddressingMode.PreDecrement, op & 7),
                new Operand(AddressingMode.PreDecrement, RegX(op)));
    }

    private static Instruction? DecodeShift(ushort op, ref Reader r)
    {
        var left = (op & 0x0100) != 0;
        if (((op >> 6) & 3) == 3)
        {
            // A memory shift moves one bit of a word. Bit 11 set is a bit field instruction of the 68020.
            if ((op & 0x0800) != 0)
                return null;
            var destination = ReadEa(op, Size.Word, Ea.MemoryAlterable, ref r);
            return destination == null
                ? null
                : Make(ShiftOperation((op >> 9) & 3, left), Size.Word, Operand.Imm(1), destination.Value);
        }

        TryStandardSize(op, out var size);
        var countOrRegister = RegX(op);
        var count = (op & 0x20) == 0
            ? Operand.Imm(countOrRegister == 0 ? 8u : (uint)countOrRegister)
            : Operand.Dn(countOrRegister);
        return Make(ShiftOperation((op >> 3) & 3, left), size, count, Operand.Dn(op & 7));
    }

    private static Operation ShiftOperation(int type, bool left)
    {
        return type switch
        {
            0 => left ? Operation.Asl : Operation.Asr,
            1 => left ? Operation.Lsl : Operation.Lsr,
            2 => left ? Operation.Roxl : Operation.Roxr,
            _ => left ? Operation.Rol : Operation.Ror,
        };
    }

    private static Operation BitOperation(ushort op)
    {
        return ((op >> 6) & 3) switch
        {
            0 => Operation.Btst,
            1 => Operation.Bchg,
            2 => Operation.Bclr,
            _ => Operation.Bset,
        };
    }

    private static Instruction? MakeSingle(Operation operation, Size size, ushort op, Ea allowed, ref Reader r,
        bool asSource = false)
    {
        var operand = ReadEa(op, size, allowed, ref r);
        if (operand == null)
            return null;
        return asSource
            ? Make(operation, size, operand.Value)
            : Make(operation, size, Operand.None, operand.Value);
    }

    private static Instruction Make(Operation operation, Size size = Size.Word, Operand source = default,
        Operand destination = default)
    {
        // The caller sets the address, the opcode and the length.
        return new Instruction
        {
            Address = 0, Opcode = 0, Length = 0,
            Operation = operation, Size = size, Source = source, Destination = destination,
        };
    }

    private static Operand? ReadEa(ushort op, Size size, Ea allowed, ref Reader r)
    {
        return ReadEa(EaMode(op), op & 7, size, allowed, ref r);
    }

    private static Operand? ReadEa(int mode, int register, Size size, Ea allowed, ref Reader r)
    {
        var kind = mode < 7
            ? (Ea)(1 << mode)
            : register switch
            {
                0 => Ea.AbsW,
                1 => Ea.AbsL,
                2 => Ea.PcDisp,
                3 => Ea.PcIndex,
                4 => Ea.Imm,
                _ => Ea.None,
            };
        if (kind == Ea.None || (kind & allowed) == 0)
            return null;

        switch (kind)
        {
            case Ea.Dn: return Operand.Dn(register);
            case Ea.An: return Operand.An(register);
            case Ea.Ind: return new Operand(AddressingMode.Indirect, register);
            case Ea.PostInc: return new Operand(AddressingMode.PostIncrement, register);
            case Ea.PreDec: return new Operand(AddressingMode.PreDecrement, register);
            case Ea.Disp: return new Operand(AddressingMode.Displacement, register, (short)r.Next());
            case Ea.Index: return ReadIndex(AddressingMode.Indexed, register, 0, ref r);
            case Ea.AbsW: return new Operand(AddressingMode.AbsoluteShort, Value: (uint)(short)r.Next());
            case Ea.AbsL: return new Operand(AddressingMode.AbsoluteLong, Value: r.NextLong());
            case Ea.PcDisp:
            {
                var basePc = r.Position;
                var displacement = (short)r.Next();
                return new Operand(AddressingMode.PcDisplacement, Displacement: displacement,
                    Value: basePc + (uint)displacement);
            }
            case Ea.PcIndex: return ReadIndex(AddressingMode.PcIndexed, 0, r.Position, ref r);
            default: return Operand.Imm(r.ReadImmediate(size));
        }
    }

    /// <summary>Reads a brief extension word: the index register, the index size and an 8-bit displacement.</summary>
    private static Operand ReadIndex(AddressingMode mode, int register, uint basePc, ref Reader r)
    {
        var extension = r.Next();
        var displacement = (sbyte)extension;
        // Bits 10 to 8 (the scale on the 68020) have no effect on the 68000.
        return new Operand(mode, register, displacement,
            IndexRegister: (extension >> 12) & 0xF,
            IndexSize: (extension & 0x0800) == 0 ? Size.Word : Size.Long,
            Value: mode == AddressingMode.PcIndexed ? basePc + (uint)displacement : 0);
    }

    private static bool TryStandardSize(ushort op, out Size size)
    {
        var bits = (op >> 6) & 3;
        size = bits < 3 ? StandardSizes[bits] : Size.Long;
        return bits < 3;
    }

    private static int EaMode(ushort op) => (op >> 3) & 7;

    private static int RegX(ushort op) => (op >> 9) & 7;

    private static ushort ReverseBits(ushort value)
    {
        ushort result = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((value & (1 << i)) != 0)
                result |= (ushort)(1 << (15 - i));
        }

        return result;
    }

    private struct Reader(uint position, Func<uint, ushort> readWord)
    {
        public uint Position { get; private set; } = position;

        public ushort Next()
        {
            var word = readWord(Position);
            Position += 2;
            return word;
        }

        public uint NextLong()
        {
            var high = (uint)Next();
            return high << 16 | Next();
        }

        /// <summary>A byte immediate uses the low byte of one extension word.</summary>
        public uint ReadImmediate(Size size) => size switch
        {
            Size.Byte => (uint)(Next() & 0xFF),
            Size.Word => Next(),
            _ => NextLong(),
        };
    }
}
