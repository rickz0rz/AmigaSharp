using System.Numerics;

namespace AmigaSharp.Runtime.Cpu;

/// <summary>
/// An estimate of the clock cycles of a 68000 instruction. The runtime uses it to run the CPU at the speed of a real
/// 68000, not as fast as the host can.
/// </summary>
/// <remarks>
/// The estimate uses the main rules of the 68000 timing tables: 4 cycles for each word that the CPU reads from the
/// instruction stream, and 4 cycles for each memory access of a byte or a word (8 for a long). Some operations add a
/// known cost, for example MULU and DIVU. The estimate does not know if a branch is taken, the count of a shift in a
/// register, or the wait states of chip memory. So it is near the real time of a program, not equal to it.
/// </remarks>
public static class CycleEstimate
{
    /// <summary>The clock of the 68000 of an NTSC Amiga, in Hz.</summary>
    public const double ClockHz = 7_159_090.5;

    public static int Of(Instruction instruction)
    {
        var cycles = instruction.Length / 2 * 4;
        var access = instruction.Size == Size.Long ? 8 : 4;
        var source = instruction.Source;
        var destination = instruction.Destination;

        // LEA, PEA, JMP and JSR calculate an address. They do not read the memory at the address.
        var readsSource = instruction.Operation is not (Operation.Lea or Operation.Pea or Operation.Jmp or Operation.Jsr);
        cycles += Address(source.Mode) + (readsSource && IsMemory(source.Mode) ? access : 0);
        if (IsMemory(destination.Mode))
        {
            cycles += Address(destination.Mode);
            // MOVE, CLR and Scc only write the destination. The other operations read it and write it.
            cycles += instruction.Operation is Operation.Move or Operation.Clr or Operation.Scc ? access : 2 * access;
        }
        else if (instruction.Size == Size.Long && destination.Mode is AddressingMode.DataRegister or AddressingMode.AddressRegister
                 && instruction.Operation is not (Operation.Move or Operation.Movea or Operation.Moveq or Operation.Lea))
        {
            // A long operation of the ALU on a register takes more cycles. A MOVE does not use the ALU.
            cycles += 4;
        }

        return cycles + instruction.Operation switch
        {
            Operation.Mulu or Operation.Muls => 66,
            Operation.Divu => 136,
            Operation.Divs => 154,
            Operation.Movem => BitOperations.PopCount(instruction.RegisterMask) * access,
            Operation.Asl or Operation.Asr or Operation.Lsl or Operation.Lsr or Operation.Rol or Operation.Ror
                or Operation.Roxl or Operation.Roxr => ShiftCost(instruction),
            Operation.Bcc or Operation.Dbcc => 6,
            Operation.Bsr or Operation.Jsr or Operation.Pea => 12,
            Operation.Rts or Operation.Rtr or Operation.Rte => 12,
            Operation.Link => 12,
            Operation.Unlk => 8,
            Operation.Lea => 0,
            Operation.Trap => 30,
            Operation.Chk => 6,
            Operation.Exg or Operation.Swap or Operation.Ext => 2,
            _ => 0,
        };
    }

    /// <summary>A shift in a register takes 2 cycles for each bit. For a count in a register, the estimate is 8 bits.</summary>
    private static int ShiftCost(Instruction instruction) => instruction.Source.Mode switch
    {
        AddressingMode.Immediate => 2 + 2 * (int)instruction.Source.Value,
        AddressingMode.DataRegister => 2 + 2 * 8,
        _ => 0,
    };

    /// <summary>The extra cycles to calculate the address.</summary>
    private static int Address(AddressingMode mode) => mode switch
    {
        AddressingMode.PreDecrement => 2,
        AddressingMode.Indexed or AddressingMode.PcIndexed => 2,
        _ => 0,
    };

    private static bool IsMemory(AddressingMode mode) =>
        mode is AddressingMode.Indirect or AddressingMode.PostIncrement or AddressingMode.PreDecrement
            or AddressingMode.Displacement or AddressingMode.Indexed or AddressingMode.AbsoluteShort
            or AddressingMode.AbsoluteLong or AddressingMode.PcDisplacement or AddressingMode.PcIndexed;
}
