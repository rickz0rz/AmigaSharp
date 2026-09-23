using System.Runtime.CompilerServices;

namespace AmigaSharp.Runtime.Cpu;

/// <summary>The operand size of an instruction. The value is the number of bytes.</summary>
public enum Size : byte
{
    Byte = 1,
    Word = 2,
    Long = 4,
}

public static class SizeExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Mask(this Size size) => size switch
    {
        Size.Byte => 0xFF,
        Size.Word => 0xFFFF,
        _ => 0xFFFF_FFFF,
    };

    /// <summary>The most significant bit, which is the sign bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Msb(this Size size) => size switch
    {
        Size.Byte => 0x80,
        Size.Word => 0x8000,
        _ => 0x8000_0000,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bits(this Size size) => (int)size * 8;

    /// <summary>Sign-extends the low bits of the value to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SignExtend(this Size size, uint value) => size switch
    {
        Size.Byte => (uint)(sbyte)value,
        Size.Word => (uint)(short)value,
        _ => value,
    };

    public static string Suffix(this Size size) => size switch
    {
        Size.Byte => ".B",
        Size.Word => ".W",
        _ => ".L",
    };
}
