using System.Text;

namespace AmigaSharp.Runtime.Cpu;

/// <summary>
/// One decoded 68000 instruction.
/// </summary>
public sealed record Instruction
{
    /// <summary>The address of the first word of the instruction.</summary>
    public required uint Address { get; init; }

    public required ushort Opcode { get; init; }
    public required Operation Operation { get; init; }

    /// <summary>The length of the instruction in bytes, including all extension words.</summary>
    public required int Length { get; init; }

    public Size Size { get; init; } = Size.Word;
    public Operand Source { get; init; }
    public Operand Destination { get; init; }

    /// <summary>The condition of Bcc, DBcc and Scc.</summary>
    public Condition Condition { get; init; }

    /// <summary>The branch target of Bcc, BSR and DBcc.</summary>
    public uint Target { get; init; }

    /// <summary>
    /// The register list of MOVEM, in the order of the normal form: bit 0 is D0 and bit 15 is A7.
    /// The decoder reverses the mask of the predecrement form.
    /// </summary>
    public ushort RegisterMask { get; init; }

    /// <summary>True if MOVEM copies the registers to memory.</summary>
    public bool RegistersToMemory { get; init; }

    public uint NextAddress => Address + (uint)Length;

    public override string ToString()
    {
        var text = new StringBuilder(Mnemonic());
        var operands = new List<string>();
        switch (Operation)
        {
            case Operation.Bcc or Operation.Bsr:
                operands.Add($"${Target:X}");
                break;
            case Operation.Dbcc:
                operands.Add(Destination.ToString());
                operands.Add($"${Target:X}");
                break;
            case Operation.Movem:
                var list = RegisterListText();
                operands.Add(RegistersToMemory ? list : Source.ToString());
                operands.Add(RegistersToMemory ? Destination.ToString() : list);
                break;
            default:
                if (Source.Mode != AddressingMode.None)
                    operands.Add(Source.ToString());
                if (Destination.Mode != AddressingMode.None)
                    operands.Add(Destination.ToString());
                break;
        }

        if (operands.Count > 0)
            text.Append(' ').AppendJoin(',', operands);
        return text.ToString();
    }

    private string Mnemonic()
    {
        var name = Operation switch
        {
            Operation.Bcc => Condition == Condition.True ? "BRA" : "B" + Condition.ToString().ToUpperInvariant(),
            Operation.Dbcc => Condition == Condition.False ? "DBRA" : "DB" + Condition.ToString().ToUpperInvariant(),
            Operation.Scc => "S" + Condition.ToString().ToUpperInvariant(),
            _ => Operation.ToString().ToUpperInvariant(),
        };
        return HasSizeSuffix() ? name + Size.Suffix() : name;
    }

    private bool HasSizeSuffix()
    {
        return Operation switch
        {
            Operation.Bcc or Operation.Bsr or Operation.Dbcc or Operation.Scc or Operation.Jmp or Operation.Jsr
                or Operation.Lea or Operation.Pea or Operation.Nop or Operation.Rts or Operation.Rte or Operation.Rtr
                or Operation.Trap or Operation.Trapv or Operation.Illegal or Operation.LineA or Operation.LineF
                or Operation.Reset or Operation.Stop or Operation.Unlk or Operation.Link or Operation.Swap
                or Operation.Exg or Operation.Moveq or Operation.Abcd or Operation.Sbcd or Operation.Nbcd
                or Operation.Tas or Operation.Mulu or Operation.Muls or Operation.Divu or Operation.Divs
                or Operation.Chk => false,
            _ => true,
        };
    }

    private string RegisterListText()
    {
        var names = new List<string>();
        for (var i = 0; i < 16; i++)
        {
            if ((RegisterMask & (1 << i)) != 0)
                names.Add(i < 8 ? $"D{i}" : $"A{i - 8}");
        }

        return string.Join('/', names);
    }
}
