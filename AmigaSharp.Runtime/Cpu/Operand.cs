namespace AmigaSharp.Runtime.Cpu;

public enum AddressingMode : byte
{
    /// <summary>The instruction has no operand in this position.</summary>
    None,
    DataRegister,
    AddressRegister,
    Indirect,
    PostIncrement,
    PreDecrement,
    Displacement,
    Indexed,
    AbsoluteShort,
    AbsoluteLong,
    PcDisplacement,
    PcIndexed,
    Immediate,

    // These operands are not effective addresses. Some instructions use them as a source or a destination.
    Ccr,
    Sr,
    Usp,
}

/// <summary>
/// One decoded operand.
/// </summary>
/// <remarks>
/// For <see cref="AddressingMode.AbsoluteShort"/> and <see cref="AddressingMode.AbsoluteLong"/>, <see cref="Value"/>
/// is the address. For <see cref="AddressingMode.Immediate"/>, it is the value. For
/// <see cref="AddressingMode.PcDisplacement"/>, it is the effective address. For <see cref="AddressingMode.PcIndexed"/>,
/// it is the effective address without the index.
/// </remarks>
public readonly record struct Operand(
    AddressingMode Mode,
    int Register = 0,
    int Displacement = 0,
    int IndexRegister = 0,
    Size IndexSize = Size.Word,
    uint Value = 0)
{
    public static readonly Operand None = default;

    public static Operand Dn(int register) => new(AddressingMode.DataRegister, register);
    public static Operand An(int register) => new(AddressingMode.AddressRegister, register);
    public static Operand Imm(uint value) => new(AddressingMode.Immediate, Value: value);

    /// <summary>True if the index register is an address register. IndexRegister 0 to 7 is D0 to D7, and 8 to 15 is A0 to A7.</summary>
    public bool IndexIsAddressRegister => IndexRegister >= 8;

    public override string ToString()
    {
        return Mode switch
        {
            AddressingMode.None => "",
            AddressingMode.DataRegister => $"D{Register}",
            AddressingMode.AddressRegister => AddressRegisterName(Register),
            AddressingMode.Indirect => $"({AddressRegisterName(Register)})",
            AddressingMode.PostIncrement => $"({AddressRegisterName(Register)})+",
            AddressingMode.PreDecrement => $"-({AddressRegisterName(Register)})",
            AddressingMode.Displacement => $"{Displacement}({AddressRegisterName(Register)})",
            AddressingMode.Indexed => $"{Displacement}({AddressRegisterName(Register)},{IndexName()})",
            AddressingMode.AbsoluteShort => $"${Value & 0xFFFF:X4}.W",
            AddressingMode.AbsoluteLong => $"${Value:X8}.L",
            AddressingMode.PcDisplacement => $"${Value:X}(PC)",
            AddressingMode.PcIndexed => $"${Value:X}(PC,{IndexName()})",
            AddressingMode.Immediate => $"#${Value:X}",
            AddressingMode.Ccr => "CCR",
            AddressingMode.Sr => "SR",
            AddressingMode.Usp => "USP",
            _ => "?",
        };
    }

    private string IndexName()
    {
        var name = IndexIsAddressRegister ? AddressRegisterName(IndexRegister - 8) : $"D{IndexRegister}";
        return name + IndexSize.Suffix();
    }

    private static string AddressRegisterName(int register) => register == 7 ? "SP" : $"A{register}";
}
