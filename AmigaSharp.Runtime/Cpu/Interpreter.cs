namespace AmigaSharp.Runtime.Cpu;

/// <summary>
/// Runs 68000 machine code one instruction at a time.
/// </summary>
/// <remarks>
/// The runtime uses the interpreter for code that the translator did not convert, and the tests use it as the
/// reference for the translated code. An address error is fatal: <see cref="Step"/> throws
/// <see cref="AddressErrorException"/> and does not build the exception frame.
/// </remarks>
public sealed class Interpreter(CpuState cpu)
{
    private readonly Memory _memory = cpu.Memory;

    public CpuState Cpu => cpu;

    /// <summary>
    /// If true, <see cref="Step"/> throws <see cref="CpuTrapException"/> for a 68000 exception when its vector is 0, and
    /// does not jump to the handler. The runtime uses this, because it does not install exception handlers. A program
    /// that writes a handler to the vector table, for example to get into supervisor mode, gets the exception.
    /// </summary>
    public bool ExceptionsAreFatal { get; init; }

    /// <summary>Runs one instruction at <see cref="CpuState.Pc"/>, then does any trace exception.</summary>
    public void Step()
    {
        if (cpu.Stopped)
            return;

        var trace = cpu.T;
        var instruction = Decoder.Decode(cpu.Pc, _memory.Read16);
        cpu.Pc = instruction.NextAddress;
        cpu.Cycles += CycleEstimate.Of(instruction);

        try
        {
            Execute(instruction);
        }
        catch (CpuTrapException trap)
        {
            if (ExceptionsAreFatal && _memory.Read32((uint)trap.Vector * 4) == 0)
            {
                cpu.Pc = instruction.Address;
                throw;
            }

            RaiseException(trap.Vector, trap.StacksInstructionAddress ? instruction.Address : cpu.Pc);

            // TRAP, TRAPV, CHK and a zero divide complete the instruction, so a trace exception follows the trap.
            // An illegal or privileged instruction does not execute, so no trace exception follows it.
            if (trace && !trap.StacksInstructionAddress)
                RaiseException(ExceptionVector.Trace, cpu.Pc);
            return;
        }

        // The 68000 fetches the next instruction at once. A branch to an odd address fails at this point.
        if ((cpu.Pc & 1) != 0)
            throw new AddressErrorException(cpu.Pc & Memory.AddressMask);

        if (trace)
            RaiseException(ExceptionVector.Trace, cpu.Pc);
    }

    /// <summary>Does the exception processing of a group 1 or group 2 exception: it stacks PC and SR and jumps to the handler.</summary>
    public void RaiseException(int vector, uint stackedPc)
    {
        var sr = cpu.Sr;
        cpu.SetSupervisor(true);
        cpu.T = false;
        cpu.Stopped = false;
        cpu.Push32(stackedPc);
        cpu.Push16(sr);
        cpu.Pc = _memory.Read32((uint)vector * 4);
    }

    private void Execute(Instruction i)
    {
        var size = i.Size;
        switch (i.Operation)
        {
            case Operation.Move:
                ExecuteMove(i);
                break;

            case Operation.Movea:
                cpu.A[i.Destination.Register] = size.SignExtend(Read(i.Source, size));
                break;

            case Operation.Moveq:
                cpu.D[i.Destination.Register] = Ops.Logic(cpu, Size.Long, i.Source.Value);
                break;

            case Operation.Add or Operation.Addi:
                Modify(i, (s, d) => Ops.Add(cpu, size, s, d));
                break;

            case Operation.Sub or Operation.Subi:
                Modify(i, (s, d) => Ops.Sub(cpu, size, s, d));
                break;

            case Operation.Addq or Operation.Subq:
                if (i.Destination.Mode == AddressingMode.AddressRegister)
                {
                    // ADDQ and SUBQ to an address register use all 32 bits and do not change the flags.
                    var register = i.Destination.Register;
                    cpu.A[register] = i.Operation == Operation.Addq
                        ? cpu.A[register] + i.Source.Value
                        : cpu.A[register] - i.Source.Value;
                }
                else if (i.Operation == Operation.Addq)
                {
                    Modify(i, (s, d) => Ops.Add(cpu, size, s, d));
                }
                else
                {
                    Modify(i, (s, d) => Ops.Sub(cpu, size, s, d));
                }

                break;

            case Operation.Adda:
            {
                // Read the source first. A predecrement source can change the destination register.
                var source = size.SignExtend(Read(i.Source, size));
                cpu.A[i.Destination.Register] += source;
                break;
            }

            case Operation.Suba:
            {
                var source = size.SignExtend(Read(i.Source, size));
                cpu.A[i.Destination.Register] -= source;
                break;
            }

            case Operation.Addx:
                Modify(i, (s, d) => Ops.Addx(cpu, size, s, d));
                break;

            case Operation.Subx:
                Modify(i, (s, d) => Ops.Subx(cpu, size, s, d));
                break;

            case Operation.Abcd:
                Modify(i, (s, d) => Ops.Abcd(cpu, s, d));
                break;

            case Operation.Sbcd:
                Modify(i, (s, d) => Ops.Sbcd(cpu, s, d));
                break;

            case Operation.Nbcd:
                ModifySingle(i, v => Ops.Nbcd(cpu, v));
                break;

            case Operation.And or Operation.Andi:
                ExecuteLogical(i, (s, d) => s & d);
                break;

            case Operation.Or or Operation.Ori:
                ExecuteLogical(i, (s, d) => s | d);
                break;

            case Operation.Eor or Operation.Eori:
                ExecuteLogical(i, (s, d) => s ^ d);
                break;

            case Operation.Cmp or Operation.Cmpi or Operation.Cmpm:
            {
                var source = Read(i.Source, size);
                var destination = Read(i.Destination, size);
                Ops.Compare(cpu, size, source, destination);
                break;
            }

            case Operation.Cmpa:
            {
                var source = size.SignExtend(Read(i.Source, size));
                Ops.Compare(cpu, Size.Long, source, cpu.A[i.Destination.Register]);
                break;
            }

            case Operation.Clr:
                Write(i.Destination, size, 0);
                Ops.Logic(cpu, size, 0);
                break;

            case Operation.Neg:
                ModifySingle(i, v => Ops.Neg(cpu, size, v));
                break;

            case Operation.Negx:
                ModifySingle(i, v => Ops.Negx(cpu, size, v));
                break;

            case Operation.Not:
                ModifySingle(i, v => Ops.Logic(cpu, size, ~v));
                break;

            case Operation.Tst:
                Ops.Logic(cpu, size, Read(i.Destination, size));
                break;

            case Operation.Tas:
                ModifySingle(i, v =>
                {
                    Ops.Logic(cpu, Size.Byte, v);
                    return v | 0x80;
                });
                break;

            case Operation.Scc:
                Write(i.Destination, Size.Byte, Ops.TestCondition(cpu, i.Condition) ? 0xFFu : 0u);
                break;

            case Operation.Ext:
            {
                var register = i.Destination.Register;
                if (size == Size.Word)
                    cpu.SetDataWord(register, Ops.Logic(cpu, Size.Word, (uint)(sbyte)cpu.D[register]));
                else
                    cpu.D[register] = Ops.Logic(cpu, Size.Long, (uint)(short)cpu.D[register]);
                break;
            }

            case Operation.Swap:
            {
                var value = cpu.D[i.Destination.Register];
                cpu.D[i.Destination.Register] = Ops.Logic(cpu, Size.Long, value << 16 | value >> 16);
                break;
            }

            case Operation.Exg:
            {
                var source = ReadRegister(i.Source);
                var destination = ReadRegister(i.Destination);
                WriteRegister(i.Source, destination);
                WriteRegister(i.Destination, source);
                break;
            }

            case Operation.Mulu:
                ModifyDataRegister(i, (s, d) => Ops.Mulu(cpu, s, d));
                break;

            case Operation.Muls:
                ModifyDataRegister(i, (s, d) => Ops.Muls(cpu, s, d));
                break;

            case Operation.Divu:
                ModifyDataRegister(i, (s, d) => Ops.Divu(cpu, s, d));
                break;

            case Operation.Divs:
                ModifyDataRegister(i, (s, d) => Ops.Divs(cpu, s, d));
                break;

            case Operation.Chk:
                Ops.Chk(cpu, Read(i.Source, Size.Word), cpu.D[i.Destination.Register]);
                break;

            case Operation.Btst or Operation.Bchg or Operation.Bclr or Operation.Bset:
                ExecuteBit(i);
                break;

            case Operation.Asl: ExecuteShift(i, Ops.Asl); break;
            case Operation.Asr: ExecuteShift(i, Ops.Asr); break;
            case Operation.Lsl: ExecuteShift(i, Ops.Lsl); break;
            case Operation.Lsr: ExecuteShift(i, Ops.Lsr); break;
            case Operation.Rol: ExecuteShift(i, Ops.Rol); break;
            case Operation.Ror: ExecuteShift(i, Ops.Ror); break;
            case Operation.Roxl: ExecuteShift(i, Ops.Roxl); break;
            case Operation.Roxr: ExecuteShift(i, Ops.Roxr); break;

            case Operation.Lea:
                cpu.A[i.Destination.Register] = EffectiveAddress(i.Source);
                break;

            case Operation.Pea:
                cpu.Push32(EffectiveAddress(i.Source));
                break;

            case Operation.Link:
            {
                // LINK A7 stores the value of A7 after the decrement.
                var register = i.Destination.Register;
                var value = cpu.A[register];
                cpu.Sp -= 4;
                _memory.Write32(cpu.Sp, register == 7 ? cpu.Sp : value);
                cpu.A[register] = cpu.Sp;
                cpu.Sp += i.Source.Value;
                break;
            }

            case Operation.Unlk:
            {
                var register = i.Destination.Register;
                var value = _memory.Read32(cpu.A[register]);
                cpu.Sp = cpu.A[register] + 4;
                cpu.A[register] = value;
                break;
            }

            case Operation.Movem:
                ExecuteMovem(i);
                break;

            case Operation.Movep:
                ExecuteMovep(i);
                break;

            case Operation.Bcc:
                if (Ops.TestCondition(cpu, i.Condition))
                    cpu.Pc = i.Target;
                break;

            case Operation.Bsr:
                cpu.Push32(cpu.Pc);
                cpu.Pc = i.Target;
                break;

            case Operation.Dbcc:
                if (!Ops.TestCondition(cpu, i.Condition))
                {
                    var register = i.Destination.Register;
                    var counter = (cpu.D[register] - 1) & 0xFFFF;
                    cpu.SetDataWord(register, counter);
                    if (counter != 0xFFFF)
                        cpu.Pc = i.Target;
                }

                break;

            case Operation.Jmp:
                cpu.Pc = EffectiveAddress(i.Source);
                break;

            case Operation.Jsr:
            {
                var target = EffectiveAddress(i.Source);
                cpu.Push32(cpu.Pc);
                cpu.Pc = target;
                break;
            }

            case Operation.Rts:
                cpu.Pc = cpu.Pop32();
                break;

            case Operation.Rtr:
                cpu.Ccr = (byte)cpu.Pop16();
                cpu.Pc = cpu.Pop32();
                break;

            case Operation.Rte:
            {
                RequireSupervisor();
                var sr = cpu.Pop16();
                cpu.Pc = cpu.Pop32();
                cpu.Sr = sr;
                break;
            }

            case Operation.Trap:
                throw new CpuTrapException(ExceptionVector.Trap0 + (int)i.Source.Value);

            case Operation.Trapv:
                if (cpu.V)
                    throw new CpuTrapException(ExceptionVector.Trapv);
                break;

            case Operation.Nop:
                break;

            case Operation.Reset:
                // RESET resets the external devices. It has no effect on the CPU state.
                RequireSupervisor();
                break;

            case Operation.Stop:
                RequireSupervisor();
                cpu.Sr = (ushort)i.Source.Value;
                cpu.Stopped = true;
                break;

            case Operation.LineA:
                throw new CpuTrapException(ExceptionVector.LineA);

            case Operation.LineF:
                throw new CpuTrapException(ExceptionVector.LineF);

            default:
                throw new CpuTrapException(ExceptionVector.IllegalInstruction);
        }
    }

    private void ExecuteMove(Instruction i)
    {
        var size = i.Size;
        switch (i.Destination.Mode)
        {
            case AddressingMode.Sr:
                RequireSupervisor();
                cpu.Sr = (ushort)Read(i.Source, Size.Word);
                return;
            case AddressingMode.Ccr:
                cpu.Ccr = (byte)Read(i.Source, Size.Word);
                return;
            case AddressingMode.Usp:
                RequireSupervisor();
                cpu.Usp = cpu.A[i.Source.Register];
                return;
        }

        switch (i.Source.Mode)
        {
            case AddressingMode.Sr:
                // MOVE from SR is not privileged on the 68000.
                Write(i.Destination, Size.Word, cpu.Sr);
                return;
            case AddressingMode.Usp:
                RequireSupervisor();
                cpu.A[i.Destination.Register] = cpu.Usp;
                return;
        }

        var value = Read(i.Source, size);
        Write(i.Destination, size, value);
        Ops.Logic(cpu, size, value);
    }

    private void ExecuteLogical(Instruction i, Func<uint, uint, uint> operation)
    {
        switch (i.Destination.Mode)
        {
            case AddressingMode.Ccr:
                cpu.Ccr = (byte)operation(i.Source.Value, cpu.Ccr);
                return;
            case AddressingMode.Sr:
                RequireSupervisor();
                cpu.Sr = (ushort)operation(i.Source.Value, cpu.Sr);
                return;
        }

        Modify(i, (s, d) => Ops.Logic(cpu, i.Size, operation(s, d)));
    }

    private void ExecuteBit(Instruction i)
    {
        var bitNumber = (int)Read(i.Source, Size.Long);
        if (i.Destination.Mode == AddressingMode.DataRegister)
        {
            var register = i.Destination.Register;
            var bit = 1u << (bitNumber & 31);
            cpu.Z = (cpu.D[register] & bit) == 0;
            cpu.D[register] = ChangeBit(i.Operation, cpu.D[register], bit);
            return;
        }

        var location = Resolve(i.Destination, Size.Byte);
        var value = Read(location, Size.Byte);
        var memoryBit = 1u << (bitNumber & 7);
        cpu.Z = (value & memoryBit) == 0;
        if (i.Operation != Operation.Btst)
            Write(location, Size.Byte, ChangeBit(i.Operation, value, memoryBit));
    }

    private static uint ChangeBit(Operation operation, uint value, uint bit)
    {
        return operation switch
        {
            Operation.Bchg => value ^ bit,
            Operation.Bclr => value & ~bit,
            Operation.Bset => value | bit,
            _ => value,
        };
    }

    private void ExecuteShift(Instruction i, Func<CpuState, Size, uint, int, uint> shift)
    {
        var count = i.Source.Mode == AddressingMode.DataRegister
            ? (int)(cpu.D[i.Source.Register] & 63)
            : (int)i.Source.Value;
        ModifySingle(i, v => shift(cpu, i.Size, v, count));
    }

    private void ExecuteMovem(Instruction i)
    {
        var size = i.Size;
        var step = (uint)size;
        var mask = i.RegisterMask;

        if (i.RegistersToMemory)
        {
            if (i.Destination.Mode == AddressingMode.PreDecrement)
            {
                // The registers go to descending addresses, from A7 to D0. The 68000 stores the original value of
                // the address register if it is in the list.
                var register = i.Destination.Register;
                var address = cpu.A[register];
                for (var r = 15; r >= 0; r--)
                {
                    if ((mask & (1 << r)) == 0)
                        continue;
                    address -= step;
                    WriteMemory(address, size, GetRegister(r));
                }

                cpu.A[register] = address;
                return;
            }

            var target = EffectiveAddress(i.Destination);
            for (var r = 0; r < 16; r++)
            {
                if ((mask & (1 << r)) == 0)
                    continue;
                WriteMemory(target, size, GetRegister(r));
                target += step;
            }

            return;
        }

        var postIncrement = i.Source.Mode == AddressingMode.PostIncrement;
        var source = postIncrement ? cpu.A[i.Source.Register] : EffectiveAddress(i.Source);
        for (var r = 0; r < 16; r++)
        {
            if ((mask & (1 << r)) == 0)
                continue;
            // A word load sign-extends to 32 bits, also for a data register.
            SetRegister(r, size.SignExtend(ReadMemory(source, size)));
            source += step;
        }

        if (postIncrement)
            cpu.A[i.Source.Register] = source;
    }

    private void ExecuteMovep(Instruction i)
    {
        var count = (int)i.Size;
        if (i.Source.Mode == AddressingMode.DataRegister)
        {
            var value = cpu.D[i.Source.Register];
            var address = cpu.A[i.Destination.Register] + (uint)i.Destination.Displacement;
            for (var b = count - 1; b >= 0; b--)
            {
                _memory.Write8(address, (byte)(value >> (b * 8)));
                address += 2;
            }

            return;
        }

        var sourceAddress = cpu.A[i.Source.Register] + (uint)i.Source.Displacement;
        uint result = 0;
        for (var b = 0; b < count; b++)
        {
            result = result << 8 | _memory.Read8(sourceAddress);
            sourceAddress += 2;
        }

        if (i.Size == Size.Word)
            cpu.SetDataWord(i.Destination.Register, result);
        else
            cpu.D[i.Destination.Register] = result;
    }

    /// <summary>Reads the source, then reads and writes the destination once.</summary>
    private void Modify(Instruction i, Func<uint, uint, uint> operation)
    {
        var source = Read(i.Source, i.Size);
        var location = Resolve(i.Destination, i.Size);
        Write(location, i.Size, operation(source, Read(location, i.Size)));
    }

    private void ModifySingle(Instruction i, Func<uint, uint> operation)
    {
        var location = Resolve(i.Destination, i.Size);
        Write(location, i.Size, operation(Read(location, i.Size)));
    }

    /// <summary>MUL and DIV: a word source and a 32-bit data register.</summary>
    private void ModifyDataRegister(Instruction i, Func<uint, uint, uint> operation)
    {
        var source = Read(i.Source, Size.Word);
        var register = i.Destination.Register;
        cpu.D[register] = operation(source, cpu.D[register]);
    }

    private void RequireSupervisor()
    {
        if (!cpu.S)
            throw new CpuTrapException(ExceptionVector.PrivilegeViolation);
    }

    // Operand access.

    private enum LocationKind : byte
    {
        DataRegister,
        AddressRegister,
        Memory,
        Immediate,
    }

    private readonly record struct Location(LocationKind Kind, int Register, uint Address);

    /// <summary>Finds the location of an operand. Postincrement and predecrement change the register here, one time.</summary>
    private Location Resolve(in Operand operand, Size size)
    {
        switch (operand.Mode)
        {
            case AddressingMode.DataRegister:
                return new Location(LocationKind.DataRegister, operand.Register, 0);
            case AddressingMode.AddressRegister:
                return new Location(LocationKind.AddressRegister, operand.Register, 0);
            case AddressingMode.Immediate:
                return new Location(LocationKind.Immediate, 0, operand.Value);
            case AddressingMode.PostIncrement:
            {
                var address = cpu.A[operand.Register];
                cpu.A[operand.Register] += Increment(operand.Register, size);
                return new Location(LocationKind.Memory, 0, address);
            }
            case AddressingMode.PreDecrement:
                cpu.A[operand.Register] -= Increment(operand.Register, size);
                return new Location(LocationKind.Memory, 0, cpu.A[operand.Register]);
            default:
                return new Location(LocationKind.Memory, 0, EffectiveAddress(operand));
        }
    }

    /// <summary>A byte access through A7 moves the stack pointer by 2, so the stack stays word-aligned.</summary>
    private static uint Increment(int register, Size size) => size == Size.Byte && register == 7 ? 2u : (uint)size;

    /// <summary>The address of a memory operand that has no side effects.</summary>
    private uint EffectiveAddress(in Operand operand)
    {
        return operand.Mode switch
        {
            AddressingMode.Indirect => cpu.A[operand.Register],
            AddressingMode.Displacement => cpu.A[operand.Register] + (uint)operand.Displacement,
            AddressingMode.Indexed => cpu.A[operand.Register] + (uint)operand.Displacement + Index(operand),
            AddressingMode.AbsoluteShort or AddressingMode.AbsoluteLong or AddressingMode.PcDisplacement => operand.Value,
            AddressingMode.PcIndexed => operand.Value + Index(operand),
            _ => throw new InvalidOperationException($"{operand.Mode} has no effective address."),
        };
    }

    private uint Index(in Operand operand)
    {
        var value = operand.IndexIsAddressRegister ? cpu.A[operand.IndexRegister - 8] : cpu.D[operand.IndexRegister];
        return operand.IndexSize == Size.Word ? (uint)(short)value : value;
    }

    private uint Read(in Operand operand, Size size) => Read(Resolve(operand, size), size);

    private void Write(in Operand operand, Size size, uint value) => Write(Resolve(operand, size), size, value);

    private uint Read(in Location location, Size size)
    {
        return location.Kind switch
        {
            LocationKind.DataRegister => cpu.D[location.Register] & size.Mask(),
            LocationKind.AddressRegister => cpu.A[location.Register] & size.Mask(),
            LocationKind.Immediate => location.Address & size.Mask(),
            _ => ReadMemory(location.Address, size),
        };
    }

    private void Write(in Location location, Size size, uint value)
    {
        switch (location.Kind)
        {
            case LocationKind.DataRegister:
                switch (size)
                {
                    case Size.Byte: cpu.SetDataByte(location.Register, value); break;
                    case Size.Word: cpu.SetDataWord(location.Register, value); break;
                    default: cpu.D[location.Register] = value; break;
                }

                break;
            case LocationKind.AddressRegister:
                cpu.A[location.Register] = size.SignExtend(value);
                break;
            case LocationKind.Memory:
                WriteMemory(location.Address, size, value);
                break;
            default:
                throw new InvalidOperationException("Cannot write to an immediate operand.");
        }
    }

    private uint ReadMemory(uint address, Size size)
    {
        return size switch
        {
            Size.Byte => _memory.Read8(address),
            Size.Word => _memory.Read16(address),
            _ => _memory.Read32(address),
        };
    }

    private void WriteMemory(uint address, Size size, uint value)
    {
        switch (size)
        {
            case Size.Byte: _memory.Write8(address, (byte)value); break;
            case Size.Word: _memory.Write16(address, (ushort)value); break;
            default: _memory.Write32(address, value); break;
        }
    }

    private uint ReadRegister(in Operand operand) =>
        operand.Mode == AddressingMode.DataRegister ? cpu.D[operand.Register] : cpu.A[operand.Register];

    private void WriteRegister(in Operand operand, uint value)
    {
        if (operand.Mode == AddressingMode.DataRegister)
            cpu.D[operand.Register] = value;
        else
            cpu.A[operand.Register] = value;
    }

    /// <summary>Register numbers 0 to 7 are D0 to D7, and 8 to 15 are A0 to A7.</summary>
    private uint GetRegister(int number) => number < 8 ? cpu.D[number] : cpu.A[number - 8];

    private void SetRegister(int number, uint value)
    {
        if (number < 8)
            cpu.D[number] = value;
        else
            cpu.A[number - 8] = value;
    }
}
