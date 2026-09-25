using System.Text;
using AmigaSharp.Runtime.Dos;

namespace AmigaSharp.Runtime.Exec;

/// <summary>
/// The formatter of exec RawDoFmt. The format commands are <c>%[flags][width][.limit][l]type</c>:
/// <list type="bullet">
/// <item>The flag <c>-</c> aligns the value to the left. A width that starts with 0 fills with zeros.</item>
/// <item>The value is 16 bits (a word on the data stream) unless the command has <c>l</c>.</item>
/// <item>The types are <c>d</c>, <c>u</c>, <c>x</c>, <c>X</c>, <c>c</c>, <c>s</c> (a C string) and <c>b</c> (a BSTR).</item>
/// </list>
/// </summary>
public static class RawDoFormat
{
    /// <summary>Formats the text. Returns the text and the address after the last value on the data stream.</summary>
    public static (byte[] Text, uint NextData) Format(Memory memory, uint format, uint data)
    {
        var output = new List<byte>();
        var position = format;
        while (true)
        {
            var c = memory.Read8(position++);
            if (c == 0)
                break;
            if (c != '%')
            {
                output.Add(c);
                continue;
            }

            var leftAlign = false;
            var zeroFill = false;
            if (memory.Read8(position) == '-')
            {
                leftAlign = true;
                position++;
            }

            if (memory.Read8(position) == '0')
            {
                zeroFill = true;
                position++;
            }

            var width = ReadNumber(memory, ref position);
            int? limit = null;
            if (memory.Read8(position) == '.')
            {
                position++;
                limit = ReadNumber(memory, ref position);
            }

            var isLong = false;
            if (memory.Read8(position) == 'l')
            {
                isLong = true;
                position++;
            }

            var type = (char)memory.Read8(position++);
            byte[] field;
            switch (type)
            {
                case 'd' or 'D':
                    field = Ascii(isLong ? ((int)Next(memory, ref data, true)).ToString() : ((short)Next(memory, ref data, false)).ToString());
                    break;
                case 'u' or 'U':
                    field = Ascii(Next(memory, ref data, isLong).ToString());
                    break;
                case 'x':
                    field = Ascii(Next(memory, ref data, isLong).ToString("x"));
                    break;
                case 'X':
                    field = Ascii(Next(memory, ref data, isLong).ToString("X"));
                    break;
                case 'c':
                    field = [(byte)Next(memory, ref data, isLong)];
                    break;
                case 's':
                {
                    var pointer = Next(memory, ref data, true);
                    field = pointer == 0 ? [] : memory.ReadCStringBytes(pointer);
                    break;
                }
                case 'b':
                {
                    var bstr = Next(memory, ref data, true);
                    field = Encoding.Latin1.GetBytes(FileSystem.ReadBstr(memory, bstr));
                    break;
                }
                case '%':
                    field = [(byte)'%'];
                    break;
                case '\0':
                    // The format ends after the percent sign.
                    position--;
                    field = [];
                    break;
                default:
                    field = [(byte)type];
                    break;
            }

            if (limit is { } maximum && type is 's' or 'b' && field.Length > maximum)
                field = field[..maximum];

            var padding = Math.Max(0, width - field.Length);
            if (leftAlign)
            {
                output.AddRange(field);
                output.AddRange(Enumerable.Repeat((byte)' ', padding));
            }
            else if (zeroFill && field.Length > 0 && field[0] == '-' && type is 'd' or 'D')
            {
                // The sign goes before the zeros.
                output.Add((byte)'-');
                output.AddRange(Enumerable.Repeat((byte)'0', padding));
                output.AddRange(field[1..]);
            }
            else
            {
                output.AddRange(Enumerable.Repeat(zeroFill ? (byte)'0' : (byte)' ', padding));
                output.AddRange(field);
            }
        }

        return (output.ToArray(), data);
    }

    private static int ReadNumber(Memory memory, ref uint position)
    {
        var value = 0;
        while (memory.Read8(position) is >= (byte)'0' and <= (byte)'9')
            value = value * 10 + (memory.Read8(position++) - '0');
        return value;
    }

    /// <summary>Reads the next value from the data stream: a long, or a word.</summary>
    private static uint Next(Memory memory, ref uint data, bool isLong)
    {
        if (isLong)
        {
            var value = memory.Read32(data);
            data += 4;
            return value;
        }

        var word = memory.Read16(data);
        data += 2;
        return word;
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
