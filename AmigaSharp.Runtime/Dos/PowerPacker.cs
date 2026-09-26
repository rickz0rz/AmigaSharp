namespace AmigaSharp.Runtime.Dos;

/// <summary>
/// Unpacks PowerPacker data (files that start with "PP20"). The packed bits are read backwards from the end of the
/// data, and the unpacked bytes are written backwards from the end of the output.
/// </summary>
/// <remarks>
/// The file has the ID "PP20", then 4 bytes with the bit lengths of the offsets, then the packed data. The last 4
/// bytes give the size of the unpacked data (24 bits) and the number of bits to skip at the start (8 bits).
/// </remarks>
public static class PowerPacker
{
    private const int HeaderSize = 8;
    private const int TrailerSize = 4;

    public static bool IsPacked(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize + TrailerSize && data[0] == 'P' && data[1] == 'P' && data[2] == '2' && data[3] == '0';

    /// <exception cref="InvalidDataException">The data is not PowerPacker data, or it is damaged.</exception>
    public static byte[] Unpack(ReadOnlySpan<byte> data)
    {
        if (!IsPacked(data))
            throw new InvalidDataException("The data does not start with PP20.");

        var offsetLengths = data.Slice(4, 4);
        var trailer = data[^TrailerSize..];
        var size = trailer[0] << 16 | trailer[1] << 8 | trailer[2];
        var skipBits = trailer[3];
        var output = new byte[size];
        var reader = new BitReader(data[HeaderSize..^TrailerSize]);
        var position = size;

        reader.Read(skipBits);
        while (position > 0)
        {
            // Bit 0: literal bytes follow the block of the length. The count is 1 plus 2-bit groups, until a group is not 3.
            if (reader.Read(1) == 0)
            {
                var literals = 1;
                int value;
                do
                {
                    value = reader.Read(2);
                    literals += value;
                } while (value == 3);

                for (var i = 0; i < literals; i++)
                    Put(output, ref position, (byte)reader.Read(8));
                if (position == 0)
                    break;
            }

            // A copy of earlier bytes. 2 bits give the length and select the bit length of the offset.
            var kind = reader.Read(2);
            var offsetBits = offsetLengths[kind];
            var length = kind + 2;
            int offset;
            if (kind == 3)
            {
                if (reader.Read(1) == 0)
                    offsetBits = 7;
                offset = reader.Read(offsetBits);
                int value;
                do
                {
                    value = reader.Read(3);
                    length += value;
                } while (value == 7);
            }
            else
            {
                offset = reader.Read(offsetBits);
            }

            for (var i = 0; i < length; i++)
            {
                var source = position + offset;
                if (source >= size)
                    throw new InvalidDataException("The PowerPacker data refers to bytes outside the output.");
                Put(output, ref position, output[source]);
            }
        }

        return output;
    }

    private static void Put(byte[] output, ref int position, byte value)
    {
        if (position == 0)
            throw new InvalidDataException("The PowerPacker data is longer than its size.");
        output[--position] = value;
    }

    /// <summary>Reads bits from the end of the data to the start. The low bit of each byte comes first.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position = data.Length;
        private uint _buffer;
        private int _count;

        public int Read(int bits)
        {
            while (_count < bits)
            {
                if (_position == 0)
                    throw new InvalidDataException("The PowerPacker data ends too soon.");
                _buffer |= (uint)_data[--_position] << _count;
                _count += 8;
            }

            var value = 0;
            for (var i = 0; i < bits; i++)
            {
                value = (value << 1) | (int)(_buffer & 1);
                _buffer >>= 1;
            }

            _count -= bits;
            return value;
        }
    }
}
