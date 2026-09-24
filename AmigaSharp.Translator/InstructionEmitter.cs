using AmigaSharp.Runtime.Cpu;

namespace AmigaSharp.Translator;

/// <summary>
/// Writes the C# code for transfers of control. Each method returns one or more complete statements.
/// </summary>
public interface IControlFlow
{
    /// <summary>A jump to a constant address: BRA, a taken Bcc or DBcc, or JMP to a constant address.</summary>
    string Jump(Instruction instruction, uint target);

    /// <summary>A jump to an address in a local variable.</summary>
    string JumpDynamic(Instruction instruction, string target);

    /// <summary>BSR or JSR to a constant address.</summary>
    string Call(Instruction instruction, uint target);

    /// <summary>JSR to an address in a local variable.</summary>
    string CallDynamic(Instruction instruction, string target);

    /// <summary>RTS, RTR or RTE. The return address is in a local variable. The stack pointer is already updated.</summary>
    string Return(Instruction instruction, string address);
}

/// <summary>
/// Converts one 68000 instruction to C# statements. The statements use the variables <c>cpu</c> (a
/// <see cref="CpuState"/>) and <c>memory</c>, and the helpers in <see cref="Ops"/>. The result must behave the same
/// as <see cref="Interpreter"/>.
/// </summary>
public sealed class InstructionEmitter(IControlFlow flow)
{
    /// <summary>Returns the statements for the instruction. The caller puts them in a block.</summary>
    public List<string> Emit(Instruction i)
    {
        var lines = new List<string>();
        var size = i.Size;
        var s = SizeName(size);
        switch (i.Operation)
        {
            case Operation.Move:
                EmitMove(i, lines);
                break;

            case Operation.Movea:
            {
                var source = Resolve(i.Source, size, "ea", lines);
                lines.Add($"cpu.A[{i.Destination.Register}] = {SignExtend(size, source.Read)};");
                break;
            }

            case Operation.Moveq:
                lines.Add($"cpu.D[{i.Destination.Register}] = Ops.Logic(cpu, Size.Long, {Hex(i.Source.Value)});");
                break;

            case Operation.Add or Operation.Addi:
                EmitModify(i, lines, (x, y) => $"Ops.Add(cpu, {s}, {x}, {y})");
                break;

            case Operation.Sub or Operation.Subi:
                EmitModify(i, lines, (x, y) => $"Ops.Sub(cpu, {s}, {x}, {y})");
                break;

            case Operation.Addq or Operation.Subq:
                if (i.Destination.Mode == AddressingMode.AddressRegister)
                {
                    // ADDQ and SUBQ to an address register use all 32 bits and do not change the flags.
                    var sign = i.Operation == Operation.Addq ? "+" : "-";
                    lines.Add($"cpu.A[{i.Destination.Register}] {sign}= {Hex(i.Source.Value)};");
                }
                else
                {
                    var name = i.Operation == Operation.Addq ? "Add" : "Sub";
                    EmitModify(i, lines, (x, y) => $"Ops.{name}(cpu, {s}, {x}, {y})");
                }

                break;

            case Operation.Adda or Operation.Suba:
            {
                // Read the source first. A predecrement source can change the destination register.
                var source = Resolve(i.Source, size, "ea", lines);
                lines.Add($"var s = {SignExtend(size, source.Read)};");
                lines.Add($"cpu.A[{i.Destination.Register}] {(i.Operation == Operation.Adda ? "+" : "-")}= s;");
                break;
            }

            case Operation.Addx:
                EmitModify(i, lines, (x, y) => $"Ops.Addx(cpu, {s}, {x}, {y})");
                break;

            case Operation.Subx:
                EmitModify(i, lines, (x, y) => $"Ops.Subx(cpu, {s}, {x}, {y})");
                break;

            case Operation.Abcd:
                EmitModify(i, lines, (x, y) => $"Ops.Abcd(cpu, {x}, {y})");
                break;

            case Operation.Sbcd:
                EmitModify(i, lines, (x, y) => $"Ops.Sbcd(cpu, {x}, {y})");
                break;

            case Operation.Nbcd:
                EmitModifySingle(i, lines, x => $"Ops.Nbcd(cpu, {x})");
                break;

            case Operation.And or Operation.Andi:
                EmitLogical(i, lines, "&");
                break;

            case Operation.Or or Operation.Ori:
                EmitLogical(i, lines, "|");
                break;

            case Operation.Eor or Operation.Eori:
                EmitLogical(i, lines, "^");
                break;

            case Operation.Cmp or Operation.Cmpi or Operation.Cmpm:
            {
                var source = Resolve(i.Source, size, "ea", lines);
                lines.Add($"var s = {source.Read};");
                var destination = Resolve(i.Destination, size, "ea2", lines);
                lines.Add($"Ops.Compare(cpu, {s}, s, {destination.Read});");
                break;
            }

            case Operation.Cmpa:
            {
                var source = Resolve(i.Source, size, "ea", lines);
                lines.Add($"Ops.Compare(cpu, Size.Long, {SignExtend(size, source.Read)}, cpu.A[{i.Destination.Register}]);");
                break;
            }

            case Operation.Clr:
            {
                var destination = Resolve(i.Destination, size, "ea", lines);
                lines.Add(destination.Write("0u"));
                lines.Add("cpu.N = false; cpu.Z = true; cpu.V = false; cpu.C = false;");
                break;
            }

            case Operation.Neg:
                EmitModifySingle(i, lines, x => $"Ops.Neg(cpu, {s}, {x})");
                break;

            case Operation.Negx:
                EmitModifySingle(i, lines, x => $"Ops.Negx(cpu, {s}, {x})");
                break;

            case Operation.Not:
                EmitModifySingle(i, lines, x => $"Ops.Logic(cpu, {s}, ~{x})");
                break;

            case Operation.Tst:
            {
                var operand = Resolve(i.Destination, size, "ea", lines);
                lines.Add($"Ops.Logic(cpu, {s}, {operand.Read});");
                break;
            }

            case Operation.Tas:
            {
                var operand = Resolve(i.Destination, Size.Byte, "ea", lines);
                lines.Add($"var d = {operand.Read};");
                lines.Add("Ops.Logic(cpu, Size.Byte, d);");
                lines.Add(operand.Write("d | 0x80u"));
                break;
            }

            case Operation.Scc:
            {
                var operand = Resolve(i.Destination, Size.Byte, "ea", lines);
                lines.Add(operand.Write($"{ConditionExpression(i.Condition)} ? 0xFFu : 0u"));
                break;
            }

            case Operation.Ext:
            {
                var r = i.Destination.Register;
                lines.Add(size == Size.Word
                    ? $"cpu.SetDataWord({r}, Ops.Logic(cpu, Size.Word, (uint)(sbyte)cpu.D[{r}]));"
                    : $"cpu.D[{r}] = Ops.Logic(cpu, Size.Long, (uint)(short)cpu.D[{r}]);");
                break;
            }

            case Operation.Swap:
            {
                var r = i.Destination.Register;
                lines.Add($"cpu.D[{r}] = Ops.Logic(cpu, Size.Long, cpu.D[{r}] << 16 | cpu.D[{r}] >> 16);");
                break;
            }

            case Operation.Exg:
            {
                var x = RegisterName(i.Source);
                var y = RegisterName(i.Destination);
                lines.Add($"var t = {x};");
                lines.Add($"{x} = {y};");
                lines.Add($"{y} = t;");
                break;
            }

            case Operation.Mulu or Operation.Muls or Operation.Divu or Operation.Divs:
            {
                if (i.Operation is Operation.Divu or Operation.Divs)
                    lines.Add(SetPc(i.NextAddress));
                var source = Resolve(i.Source, Size.Word, "ea", lines);
                var r = i.Destination.Register;
                lines.Add($"cpu.D[{r}] = Ops.{i.Operation}(cpu, {source.Read}, cpu.D[{r}]);");
                break;
            }

            case Operation.Chk:
            {
                lines.Add(SetPc(i.NextAddress));
                var source = Resolve(i.Source, Size.Word, "ea", lines);
                lines.Add($"Ops.Chk(cpu, {source.Read}, cpu.D[{i.Destination.Register}]);");
                break;
            }

            case Operation.Btst or Operation.Bchg or Operation.Bclr or Operation.Bset:
                EmitBit(i, lines);
                break;

            case Operation.Asl or Operation.Asr or Operation.Lsl or Operation.Lsr
                or Operation.Rol or Operation.Ror or Operation.Roxl or Operation.Roxr:
            {
                var count = i.Source.Mode == AddressingMode.DataRegister
                    ? $"(int)(cpu.D[{i.Source.Register}] & 63)"
                    : i.Source.Value.ToString();
                EmitModifySingle(i, lines, x => $"Ops.{i.Operation}(cpu, {s}, {x}, {count})");
                break;
            }

            case Operation.Lea:
                lines.Add($"cpu.A[{i.Destination.Register}] = {Address(i.Source)};");
                break;

            case Operation.Pea:
                lines.Add($"cpu.Push32({Address(i.Source)});");
                break;

            case Operation.Link:
            {
                // LINK A7 stores the value of A7 after the decrement.
                var r = i.Destination.Register;
                lines.Add($"var v = cpu.A[{r}];");
                lines.Add("cpu.Sp -= 4;");
                lines.Add(r == 7 ? "memory.Write32(cpu.Sp, cpu.Sp);" : "memory.Write32(cpu.Sp, v);");
                lines.Add($"cpu.A[{r}] = cpu.Sp;");
                lines.Add($"cpu.Sp += {Hex(i.Source.Value)};");
                break;
            }

            case Operation.Unlk:
            {
                var r = i.Destination.Register;
                lines.Add($"var v = memory.Read32(cpu.A[{r}]);");
                lines.Add($"cpu.Sp = cpu.A[{r}] + 4u;");
                lines.Add($"cpu.A[{r}] = v;");
                break;
            }

            case Operation.Movem:
                EmitMovem(i, lines);
                break;

            case Operation.Movep:
                EmitMovep(i, lines);
                break;

            case Operation.Bcc:
                lines.Add(i.Condition == Condition.True
                    ? flow.Jump(i, i.Target)
                    : $"if ({ConditionExpression(i.Condition)}) {flow.Jump(i, i.Target)}");
                break;

            case Operation.Dbcc:
            {
                var r = i.Destination.Register;
                var loop = new List<string>
                {
                    $"var c = (cpu.D[{r}] - 1u) & 0xFFFFu;",
                    $"cpu.SetDataWord({r}, c);",
                    $"if (c != 0xFFFFu) {flow.Jump(i, i.Target)}",
                };
                if (i.Condition == Condition.False)
                {
                    lines.AddRange(loop);
                }
                else
                {
                    lines.Add($"if (!{ConditionExpression(i.Condition)})");
                    lines.Add("{");
                    lines.AddRange(loop.Select(line => "    " + line));
                    lines.Add("}");
                }

                break;
            }

            case Operation.Bsr:
                lines.Add(flow.Call(i, i.Target));
                break;

            case Operation.Jsr or Operation.Jmp:
            {
                var isCall = i.Operation == Operation.Jsr;
                if (ConstantAddress(i.Source) is { } target)
                {
                    lines.Add(isCall ? flow.Call(i, target) : flow.Jump(i, target));
                }
                else
                {
                    lines.Add($"var target = {Address(i.Source)};");
                    lines.Add(isCall ? flow.CallDynamic(i, "target") : flow.JumpDynamic(i, "target"));
                }

                break;
            }

            case Operation.Rts:
                lines.Add("var pc = cpu.Pop32();");
                lines.Add(flow.Return(i, "pc"));
                break;

            case Operation.Rtr:
                lines.Add("cpu.Ccr = (byte)cpu.Pop16();");
                lines.Add("var pc = cpu.Pop32();");
                lines.Add(flow.Return(i, "pc"));
                break;

            case Operation.Rte:
                lines.Add(RequireSupervisor(i));
                lines.Add("var sr = cpu.Pop16();");
                lines.Add("var pc = cpu.Pop32();");
                lines.Add("cpu.Sr = sr;");
                lines.Add(flow.Return(i, "pc"));
                break;

            case Operation.Trap:
                lines.Add(Trap(i.NextAddress, $"ExceptionVector.Trap0 + {i.Source.Value}"));
                break;

            case Operation.Trapv:
                lines.Add($"if (cpu.V) {{ {Trap(i.NextAddress, "ExceptionVector.Trapv")} }}");
                break;

            case Operation.Nop:
                break;

            case Operation.Reset:
                // RESET resets the external devices. It has no effect on the CPU state.
                lines.Add(RequireSupervisor(i));
                break;

            case Operation.Stop:
                lines.Add(RequireSupervisor(i));
                lines.Add($"cpu.Sr = (ushort){Hex(i.Source.Value & 0xFFFF)};");
                lines.Add("cpu.Stopped = true;");
                break;

            case Operation.LineA:
                lines.Add(Trap(i.Address, "ExceptionVector.LineA"));
                break;

            case Operation.LineF:
                lines.Add(Trap(i.Address, "ExceptionVector.LineF"));
                break;

            default:
                lines.Add(Trap(i.Address, "ExceptionVector.IllegalInstruction"));
                break;
        }

        return lines;
    }

    /// <summary>True if the instruction never continues at the next instruction.</summary>
    public static bool EndsFlow(Instruction i)
    {
        return i.Operation switch
        {
            Operation.Bcc => i.Condition == Condition.True,
            Operation.Jmp or Operation.Rts or Operation.Rtr or Operation.Rte or Operation.Trap or Operation.Illegal
                or Operation.LineA or Operation.LineF => true,
            _ => false,
        };
    }

    /// <summary>The target of JMP or JSR if it is a constant.</summary>
    public static uint? ConstantAddress(in Operand operand)
    {
        return operand.Mode is AddressingMode.AbsoluteShort or AddressingMode.AbsoluteLong or AddressingMode.PcDisplacement
            ? operand.Value
            : null;
    }

    public static string Hex(uint value) => $"0x{value:X}u";

    public static string SetPc(uint address) => $"cpu.Pc = {Hex(address)};";

    private void EmitMove(Instruction i, List<string> lines)
    {
        var size = i.Size;
        switch (i.Destination.Mode)
        {
            case AddressingMode.Sr:
            {
                lines.Add(RequireSupervisor(i));
                var source = Resolve(i.Source, Size.Word, "ea", lines);
                lines.Add($"cpu.Sr = unchecked((ushort){source.Read});");
                return;
            }
            case AddressingMode.Ccr:
            {
                var source = Resolve(i.Source, Size.Word, "ea", lines);
                lines.Add($"cpu.Ccr = unchecked((byte){source.Read});");
                return;
            }
            case AddressingMode.Usp:
                lines.Add(RequireSupervisor(i));
                lines.Add($"cpu.Usp = cpu.A[{i.Source.Register}];");
                return;
        }

        switch (i.Source.Mode)
        {
            case AddressingMode.Sr:
            {
                // MOVE from SR is not privileged on the 68000.
                var destination = Resolve(i.Destination, Size.Word, "ea", lines);
                lines.Add(destination.Write("cpu.Sr"));
                return;
            }
            case AddressingMode.Usp:
                lines.Add(RequireSupervisor(i));
                lines.Add($"cpu.A[{i.Destination.Register}] = cpu.Usp;");
                return;
        }

        var value = Resolve(i.Source, size, "ea", lines);
        lines.Add($"var v = {value.Read};");
        var target = Resolve(i.Destination, size, "ea2", lines);
        lines.Add(target.Write("v"));
        lines.Add($"Ops.Logic(cpu, {SizeName(size)}, v);");
    }

    private void EmitLogical(Instruction i, List<string> lines, string op)
    {
        switch (i.Destination.Mode)
        {
            case AddressingMode.Ccr:
                lines.Add($"cpu.Ccr = (byte)({Hex(i.Source.Value)} {op} cpu.Ccr);");
                return;
            case AddressingMode.Sr:
                lines.Add(RequireSupervisor(i));
                lines.Add($"cpu.Sr = (ushort)({Hex(i.Source.Value)} {op} cpu.Sr);");
                return;
        }

        EmitModify(i, lines, (x, y) => $"Ops.Logic(cpu, {SizeName(i.Size)}, {x} {op} {y})");
    }

    /// <summary>Reads the source, then reads and writes the destination once.</summary>
    private void EmitModify(Instruction i, List<string> lines, Func<string, string, string> operation)
    {
        var source = Resolve(i.Source, i.Size, "ea", lines);
        lines.Add($"var s = {source.Read};");
        var destination = Resolve(i.Destination, i.Size, "ea2", lines);
        lines.Add($"var d = {destination.Read};");
        lines.Add(destination.Write(operation("s", "d")));
    }

    private void EmitModifySingle(Instruction i, List<string> lines, Func<string, string> operation)
    {
        var operand = Resolve(i.Destination, i.Size, "ea", lines);
        lines.Add($"var d = {operand.Read};");
        lines.Add(operand.Write(operation("d")));
    }

    private void EmitBit(Instruction i, List<string> lines)
    {
        var inRegister = i.Destination.Mode == AddressingMode.DataRegister;
        var modulo = inRegister ? 31 : 7;
        var bit = i.Source.Mode == AddressingMode.Immediate
            ? Hex(1u << (int)(i.Source.Value & (uint)modulo))
            : $"(1u << (int)(cpu.D[{i.Source.Register}] & {modulo}))";
        var change = i.Operation switch
        {
            Operation.Bchg => "d ^ bit",
            Operation.Bclr => "d & ~bit",
            Operation.Bset => "d | bit",
            _ => null,
        };

        lines.Add($"var bit = {bit};");
        if (inRegister)
        {
            var r = i.Destination.Register;
            lines.Add($"var d = cpu.D[{r}];");
            lines.Add("cpu.Z = (d & bit) == 0;");
            if (change != null)
                lines.Add($"cpu.D[{r}] = {change};");
            return;
        }

        var operand = Resolve(i.Destination, Size.Byte, "ea", lines);
        lines.Add($"var d = {operand.Read};");
        lines.Add("cpu.Z = (d & bit) == 0;");
        if (change != null)
            lines.Add(operand.Write(change));
    }

    private static void EmitMovem(Instruction i, List<string> lines)
    {
        var size = i.Size;
        var step = (uint)size;
        var registers = Enumerable.Range(0, 16).Where(r => (i.RegisterMask & (1 << r)) != 0).ToList();

        if (i.RegistersToMemory)
        {
            if (i.Destination.Mode == AddressingMode.PreDecrement)
            {
                // The registers go to descending addresses, from A7 to D0. The 68000 stores the original value of
                // the address register if it is in the list.
                var r = i.Destination.Register;
                lines.Add($"var a = cpu.A[{r}];");
                foreach (var register in Enumerable.Reverse(registers))
                {
                    lines.Add($"a -= {step}u;");
                    lines.Add(WriteMemory(size, "a", GeneralRegister(register)));
                }

                lines.Add($"cpu.A[{r}] = a;");
                return;
            }

            lines.Add($"var a = {Address(i.Destination)};");
            var offset = 0u;
            foreach (var register in registers)
            {
                lines.Add(WriteMemory(size, Offset("a", (int)offset), GeneralRegister(register)));
                offset += step;
            }

            return;
        }

        var postIncrement = i.Source.Mode == AddressingMode.PostIncrement;
        lines.Add(postIncrement ? $"var a = cpu.A[{i.Source.Register}];" : $"var a = {Address(i.Source)};");
        var loadOffset = 0u;
        foreach (var register in registers)
        {
            // A word load sign-extends to 32 bits, also for a data register.
            lines.Add($"{GeneralRegister(register)} = {SignExtend(size, ReadMemory(size, Offset("a", (int)loadOffset)))};");
            loadOffset += step;
        }

        if (postIncrement)
            lines.Add($"cpu.A[{i.Source.Register}] = a + {loadOffset}u;");
    }

    private static void EmitMovep(Instruction i, List<string> lines)
    {
        var count = (int)i.Size;
        if (i.Source.Mode == AddressingMode.DataRegister)
        {
            lines.Add($"var a = {Offset($"cpu.A[{i.Destination.Register}]", i.Destination.Displacement)};");
            lines.Add($"var v = cpu.D[{i.Source.Register}];");
            for (var b = 0; b < count; b++)
                lines.Add($"memory.Write8({Offset("a", b * 2)}, (byte)(v >> {(count - 1 - b) * 8}));");
            return;
        }

        lines.Add($"var a = {Offset($"cpu.A[{i.Source.Register}]", i.Source.Displacement)};");
        var bytes = Enumerable.Range(0, count)
            .Select(b => $"(uint)memory.Read8({Offset("a", b * 2)}) << {(count - 1 - b) * 8}");
        var value = string.Join(" | ", bytes);
        var r = i.Destination.Register;
        lines.Add(i.Size == Size.Word ? $"cpu.SetDataWord({r}, {value});" : $"cpu.D[{r}] = {value};");
    }

    // Operand access.

    private sealed record Access(string Read, Func<string, string> Write);

    /// <summary>
    /// Returns the code to read and write an operand. For a memory operand, it adds a statement that stores the
    /// address in a local variable. Postincrement and predecrement change the register in that statement, one time.
    /// </summary>
    private static Access Resolve(in Operand operand, Size size, string name, List<string> lines)
    {
        switch (operand.Mode)
        {
            case AddressingMode.DataRegister:
            {
                var r = operand.Register;
                var read = size == Size.Long ? $"cpu.D[{r}]" : $"(cpu.D[{r}] & {Hex(size.Mask())})";
                return new Access(read, value => size switch
                {
                    Size.Byte => $"cpu.SetDataByte({r}, {value});",
                    Size.Word => $"cpu.SetDataWord({r}, {value});",
                    _ => $"cpu.D[{r}] = {value};",
                });
            }

            case AddressingMode.AddressRegister:
            {
                var r = operand.Register;
                var read = size == Size.Long ? $"cpu.A[{r}]" : $"(cpu.A[{r}] & {Hex(size.Mask())})";
                return new Access(read, value => $"cpu.A[{r}] = {SignExtend(size, value)};");
            }

            case AddressingMode.Immediate:
                return new Access(Hex(operand.Value & size.Mask()),
                    _ => throw new InvalidOperationException("Cannot write to an immediate operand."));

            case AddressingMode.PostIncrement:
                lines.Add($"var {name} = cpu.A[{operand.Register}];");
                lines.Add($"cpu.A[{operand.Register}] += {Increment(operand.Register, size)}u;");
                break;

            case AddressingMode.PreDecrement:
                lines.Add($"cpu.A[{operand.Register}] -= {Increment(operand.Register, size)}u;");
                lines.Add($"var {name} = cpu.A[{operand.Register}];");
                break;

            default:
                if (ConstantAddress(operand) is { } constant)
                    return MemoryAccess(size, Hex(constant));
                lines.Add($"var {name} = {Address(operand)};");
                break;
        }

        return MemoryAccess(size, name);
    }

    private static Access MemoryAccess(Size size, string address) =>
        new(ReadMemory(size, address), value => WriteMemory(size, address, value));

    /// <summary>A byte access through A7 moves the stack pointer by 2, so the stack stays word-aligned.</summary>
    private static uint Increment(int register, Size size) => size == Size.Byte && register == 7 ? 2u : (uint)size;

    /// <summary>The address of a memory operand that has no side effects.</summary>
    private static string Address(in Operand operand)
    {
        return operand.Mode switch
        {
            AddressingMode.Indirect => $"cpu.A[{operand.Register}]",
            AddressingMode.Displacement => Offset($"cpu.A[{operand.Register}]", operand.Displacement),
            AddressingMode.Indexed => $"{Offset($"cpu.A[{operand.Register}]", operand.Displacement)} + {Index(operand)}",
            AddressingMode.AbsoluteShort or AddressingMode.AbsoluteLong or AddressingMode.PcDisplacement =>
                Hex(operand.Value),
            AddressingMode.PcIndexed => $"{Hex(operand.Value)} + {Index(operand)}",
            _ => throw new InvalidOperationException($"{operand.Mode} has no effective address."),
        };
    }

    private static string Index(in Operand operand)
    {
        var register = operand.IndexIsAddressRegister
            ? $"cpu.A[{operand.IndexRegister - 8}]"
            : $"cpu.D[{operand.IndexRegister}]";
        return operand.IndexSize == Size.Word ? $"(uint)(short){register}" : register;
    }

    private static string Offset(string baseExpression, int displacement)
    {
        return displacement switch
        {
            0 => baseExpression,
            > 0 => $"{baseExpression} + {displacement}u",
            _ => $"{baseExpression} - {-(long)displacement}u",
        };
    }

    private static string ReadMemory(Size size, string address) => size switch
    {
        Size.Byte => $"(uint)memory.Read8({address})",
        Size.Word => $"(uint)memory.Read16({address})",
        _ => $"memory.Read32({address})",
    };

    private static string WriteMemory(Size size, string address, string value) => size switch
    {
        Size.Byte => $"memory.Write8({address}, (byte)({value}));",
        Size.Word => $"memory.Write16({address}, (ushort)({value}));",
        _ => $"memory.Write32({address}, {value});",
    };

    // The value can be a constant, so the casts need unchecked.
    private static string SignExtend(Size size, string value) => size switch
    {
        Size.Byte => $"unchecked((uint)(sbyte){value})",
        Size.Word => $"unchecked((uint)(short){value})",
        _ => value,
    };

    private static string RegisterName(in Operand operand) =>
        operand.Mode == AddressingMode.DataRegister ? $"cpu.D[{operand.Register}]" : $"cpu.A[{operand.Register}]";

    /// <summary>Register numbers 0 to 7 are D0 to D7, and 8 to 15 are A0 to A7.</summary>
    private static string GeneralRegister(int number) => number < 8 ? $"cpu.D[{number}]" : $"cpu.A[{number - 8}]";

    private static string SizeName(Size size) => $"Size.{size}";

    private static string RequireSupervisor(Instruction i) =>
        $"if (!cpu.S) {{ {Trap(i.Address, "ExceptionVector.PrivilegeViolation")} }}";

    /// <summary>Throws a 68000 exception. <c>cpu.Pc</c> holds the address that the exception stacks.</summary>
    private static string Trap(uint stackedPc, string vector) =>
        $"{SetPc(stackedPc)} throw new CpuTrapException({vector});";

    public static string ConditionExpression(Condition condition)
    {
        return condition switch
        {
            Condition.True => "true",
            Condition.False => "false",
            Condition.Hi => "(!cpu.C && !cpu.Z)",
            Condition.Ls => "(cpu.C || cpu.Z)",
            Condition.Cc => "!cpu.C",
            Condition.Cs => "cpu.C",
            Condition.Ne => "!cpu.Z",
            Condition.Eq => "cpu.Z",
            Condition.Vc => "!cpu.V",
            Condition.Vs => "cpu.V",
            Condition.Pl => "!cpu.N",
            Condition.Mi => "cpu.N",
            Condition.Ge => "(cpu.N == cpu.V)",
            Condition.Lt => "(cpu.N != cpu.V)",
            Condition.Gt => "(!cpu.Z && cpu.N == cpu.V)",
            _ => "(cpu.Z || cpu.N != cpu.V)",
        };
    }
}
