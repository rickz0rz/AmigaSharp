namespace AmigaSharp.Runtime.Hardware;

/// <summary>A register write of the copper, with the horizontal beam position in color clocks.</summary>
public readonly record struct CopperWrite(int Horizontal, int Offset, ushort Value);

/// <summary>
/// The copper: the coprocessor of Agnus that writes custom chip registers at beam positions. It has three
/// instructions: MOVE writes a register, WAIT waits for a beam position, and SKIP skips the next instruction if the
/// beam is at or after a position.
/// </summary>
/// <remarks>
/// A WAIT compares only bits 7 to 0 of the line. So a program waits for $FFDF first to wait for a line after 255.
/// </remarks>
public sealed class Copper(Memory memory, CustomChips custom)
{
    /// <summary>The last horizontal position of a line, in color clocks.</summary>
    public const int LineEnd = 0xE2;

    // A MOVE takes 4 color clocks: two fetches of 2 color clocks.
    private const int MoveTime = 4;

    private uint _pc;
    private bool _stopped = true;
    private bool _waiting;
    private int _waitPosition;
    private int _waitMask;

    /// <summary>Starts the copper at an address, as the start of a frame or a write to COPJMP1 does.</summary>
    public void Start(uint address)
    {
        _pc = address;
        _stopped = false;
        _waiting = false;
    }

    /// <summary>Stops the copper until the next start.</summary>
    public void Stop() => _stopped = true;

    /// <summary>
    /// Runs the copper for one line. Each write goes to the custom chips at once, and it goes to
    /// <paramref name="writes"/> with its position, so that the display can use it at the correct pixel.
    /// </summary>
    public void RunLine(int line, List<CopperWrite> writes)
    {
        var horizontal = 0;
        // A copper list with no WAIT does not stop the loop: the limit is the number of instructions in one line.
        for (var budget = 256; budget > 0 && !_stopped; budget--)
        {
            if (_waiting)
            {
                var at = FirstPosition(line, horizontal, _waitPosition, _waitMask);
                if (at < 0)
                    return;
                horizontal = at;
                _waiting = false;
            }

            if (horizontal > LineEnd)
                return;

            var first = memory.Read16(_pc);
            var second = memory.Read16(_pc + 2);
            _pc += 4;

            if ((first & 1) == 0)
            {
                Move(first & 0x1FE, second, horizontal, writes);
                horizontal += MoveTime;
                continue;
            }

            // WAIT and SKIP: bits 15 to 8 are the line and bits 7 to 1 are the horizontal position. The second word
            // has the masks. Bit 7 of the line is always compared.
            var position = (first & 0xFF00) | (first & 0x00FE);
            var mask = (second & 0x7F00) | 0x8000 | (second & 0x00FE);
            if ((second & 1) == 0)
            {
                _waitPosition = position;
                _waitMask = mask;
                _waiting = true;
            }
            else if (Beam(line, horizontal, mask) >= (position & mask))
            {
                _pc += 4;
            }

            horizontal += MoveTime;
        }
    }

    private void Move(int offset, ushort value, int horizontal, List<CopperWrite> writes)
    {
        switch (offset)
        {
            case CustomRegister.Copjmp1:
                Start(Location(CustomRegister.Cop1lc));
                return;
            case CustomRegister.Copjmp2:
                Start(Location(CustomRegister.Cop2lc));
                return;
        }

        // The copper cannot write the registers below $40.
        if (offset < 0x40)
            return;
        custom.Write(offset, value);
        writes.Add(new CopperWrite(horizontal, offset, value));
    }

    private uint Location(int offset) => (uint)(custom[offset] << 16 | custom[offset + 2]);

    /// <summary>The first position at or after <paramref name="horizontal"/> in the line where the beam is at or after the WAIT, or -1.</summary>
    private static int FirstPosition(int line, int horizontal, int position, int mask)
    {
        for (var h = horizontal; h <= LineEnd; h += 2)
        {
            if (Beam(line, h, mask) >= (position & mask))
                return h;
        }

        return -1;
    }

    private static int Beam(int line, int horizontal, int mask) => (((line & 0xFF) << 8) | (horizontal & 0xFE)) & mask;
}
