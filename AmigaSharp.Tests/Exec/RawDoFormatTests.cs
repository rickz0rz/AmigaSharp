using System.Text;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Exec;

namespace AmigaSharp.Tests.Exec;

public class RawDoFormatTests
{
    private const uint FormatAddress = 0x1000;
    private const uint DataAddress = 0x2000;
    private const uint StringAddress = 0x3000;

    private readonly Memory _memory = new();

    [Theory]
    [InlineData("%d", new ushort[] { 0xFFF4 }, "-12")]
    [InlineData("%u", new ushort[] { 0xFFF4 }, "65524")]
    [InlineData("%ld", new ushort[] { 0xFFFF, 0xFFF4 }, "-12")]
    [InlineData("%lu", new ushort[] { 0x0001, 0x0000 }, "65536")]
    [InlineData("%x/%X", new ushort[] { 0xBEEF, 0xBEEF }, "beef/BEEF")]
    [InlineData("%04x", new ushort[] { 0x2A }, "002a")]
    [InlineData("[%5d]", new ushort[] { 42 }, "[   42]")]
    [InlineData("[%-5d]", new ushort[] { 42 }, "[42   ]")]
    [InlineData("[%05d]", new ushort[] { 0xFFF4 }, "[-0012]")]
    [InlineData("%c%c", new ushort[] { 'o', 'k' }, "ok")]
    [InlineData("100%%", new ushort[] { }, "100%")]
    public void Format_Numbers(string format, ushort[] data, string expected)
    {
        for (var i = 0; i < data.Length; i++)
            _memory.Write16(DataAddress + (uint)i * 2, data[i]);

        var (text, next) = Format(format);

        Assert.Equal(expected, text);
        Assert.Equal(DataAddress + (uint)data.Length * 2, next);
    }

    [Fact]
    public void Format_Strings_WithWidthAndLimit()
    {
        _memory.WriteBytes(StringAddress, Encoding.Latin1.GetBytes("abcdef\0"));
        _memory.Write32(DataAddress, StringAddress);
        _memory.Write32(DataAddress + 4, StringAddress);

        Assert.Equal("[abc][  abcdef]", Format("[%.3s][%8s]").Text);
    }

    [Fact]
    public void Format_Bstr()
    {
        _memory.WriteBytes(StringAddress, [3, (byte)'D', (byte)'F', (byte)'0']);
        _memory.Write32(DataAddress, StringAddress >> 2);

        Assert.Equal("DF0:", Format("%b:").Text);
    }

    private (string Text, uint Next) Format(string format)
    {
        _memory.WriteBytes(FormatAddress, Encoding.Latin1.GetBytes(format + "\0"));
        var (text, next) = RawDoFormat.Format(_memory, FormatAddress, DataAddress);
        return (Encoding.Latin1.GetString(text), next);
    }
}
