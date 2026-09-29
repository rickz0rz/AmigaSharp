namespace AmigaSharp.Runtime.Hardware;

/// <summary>The offsets of the blitter registers from $DFF000 (hardware/custom.h).</summary>
public static class BlitterRegister
{
    public const int Bltcon0 = 0x040;
    public const int Bltcon1 = 0x042;
    public const int Bltafwm = 0x044;
    public const int Bltalwm = 0x046;
    public const int Bltcpt = 0x048;
    public const int Bltbpt = 0x04C;
    public const int Bltapt = 0x050;
    public const int Bltdpt = 0x054;
    public const int Bltsize = 0x058;
    public const int Bltsizv = 0x05C;
    public const int Bltsizh = 0x05E;
    public const int Bltcmod = 0x060;
    public const int Bltbmod = 0x062;
    public const int Bltamod = 0x064;
    public const int Bltdmod = 0x066;
    public const int Bltcdat = 0x070;
    public const int Bltbdat = 0x072;
    public const int Bltadat = 0x074;
}

/// <summary>
/// The blitter of the custom chips. A blit starts when the program writes BLTSIZE (or BLTSIZH of the ECS Agnus), and
/// it runs to the end at once. So the blitter is never busy when the program reads DMACONR.
/// </summary>
/// <remarks>
/// <para>
/// Area mode: each word of channel D is the minterm of channels A, B and C. A and B are shifted right by ASH and BSH
/// (left in descending mode), with the bits of the previous word. The first word of each row of A is masked with
/// BLTAFWM, and the last word with BLTALWM. A channel that BLTCON0 does not enable gives the value of its data
/// register. After each row, the pointers add their modulos. In descending mode, the pointers and the modulos go down.
/// The fill modes (descending mode only) fill each row of D from right to left, between the set bits.
/// </para>
/// <para>
/// Line mode: the blitter draws one pixel for each step of the length (the height of BLTSIZE). BLTAPTL is the error
/// term of the Bresenham algorithm: while it is not negative, the line takes a step on its minor axis and BLTAMOD is
/// added, else BLTBMOD is added. BLTCON1 gives the octant: SUD (the major axis is vertical), SUL and AUL (the steps
/// go up or left). ASH is the bit of the pixel in its word, and BLTCMOD is the width of a row in bytes.
/// </para>
/// <para>
/// The blitter uses DMA, so it runs only when DMACON enables DMAEN and BLTEN. A blit that starts while they are off
/// waits until the program enables them.
/// </para>
/// </remarks>
public sealed class Blitter(Memory memory, CustomChips custom)
{
    private const ushort BlitterDmaEnable = 0x0040;
    private const ushort DmaEnable = 0x0200;

    // The mask of the addresses that the blitter can reach: the 2 MB of chip RAM of the ECS Agnus.
    private const uint ChipAddressMask = 0x1F_FFFE;

    // BLTCON0.
    private const int UseA = 1 << 11;
    private const int UseB = 1 << 10;
    private const int UseC = 1 << 9;
    private const int UseD = 1 << 8;

    // BLTCON1.
    private const int LineMode = 1 << 0;
    private const int Descending = 1 << 1;
    private const int SingleDot = 1 << 1;
    private const int FillCarryIn = 1 << 2;
    private const int AlternateUpOrLeft = 1 << 2;
    private const int InclusiveFill = 1 << 3;
    private const int StepUpOrLeft = 1 << 3;
    private const int ExclusiveFill = 1 << 4;
    private const int StepVertical = 1 << 4;
    private const int Sign = 1 << 6;

    private (int Height, int Width)? _pending;

    /// <summary>The number of blits from the start. For tests and for debugging.</summary>
    public long Blits { get; private set; }

    /// <summary>The program wrote BLTSIZE: bits 15 to 6 are the height (0 is 1024) and bits 5 to 0 the width (0 is 64).</summary>
    public void WriteSize(ushort value) => Start(value >> 6 == 0 ? 1024 : value >> 6, (value & 0x3F) == 0 ? 64 : value & 0x3F);

    /// <summary>
    /// The program wrote BLTSIZH of the ECS Agnus: the width in bits 10 to 0 (0 is 2048). The height is in BLTSIZV,
    /// bits 14 to 0 (0 is 32768).
    /// </summary>
    public void WriteSizeHorizontal(ushort value)
    {
        var height = custom[BlitterRegister.Bltsizv] & 0x7FFF;
        Start(height == 0 ? 32768 : height, (value & 0x7FF) == 0 ? 2048 : value & 0x7FF);
    }

    private void Start(int height, int width)
    {
        _pending = (height, width);
        RunPending();
    }

    /// <summary>Runs a blit that waits for DMA, if DMACON now enables the blitter.</summary>
    public void RunPending()
    {
        const ushort enabled = DmaEnable | BlitterDmaEnable;
        if (_pending is not { } size || (custom.Dmacon & enabled) != enabled)
            return;
        _pending = null;
        Blits++;
        if ((custom[BlitterRegister.Bltcon1] & LineMode) != 0)
            Line(size.Height);
        else
            Area(size.Height, size.Width);
        custom.RequestInterrupt(InterruptBit.Blitter);
    }

    private void Area(int height, int width)
    {
        var control0 = custom[BlitterRegister.Bltcon0];
        var control1 = custom[BlitterRegister.Bltcon1];
        var descending = (control1 & Descending) != 0;
        var step = descending ? -2 : 2;
        var shiftA = control0 >> 12;
        var shiftB = control1 >> 12;
        var minterm = (byte)control0;
        var fill = (control1 & (InclusiveFill | ExclusiveFill)) != 0 && descending;

        var a = Pointer(BlitterRegister.Bltapt);
        var b = Pointer(BlitterRegister.Bltbpt);
        var c = Pointer(BlitterRegister.Bltcpt);
        var d = Pointer(BlitterRegister.Bltdpt);
        var moduloA = Modulo(BlitterRegister.Bltamod, descending);
        var moduloB = Modulo(BlitterRegister.Bltbmod, descending);
        var moduloC = Modulo(BlitterRegister.Bltcmod, descending);
        var moduloD = Modulo(BlitterRegister.Bltdmod, descending);
        var dataA = custom[BlitterRegister.Bltadat];
        var dataB = custom[BlitterRegister.Bltbdat];
        var dataC = custom[BlitterRegister.Bltcdat];
        var previousA = 0;
        var previousB = 0;
        var zero = true;

        for (var row = 0; row < height; row++)
        {
            var carry = (control1 & FillCarryIn) != 0;
            for (var column = 0; column < width; column++)
            {
                if ((control0 & UseA) != 0)
                {
                    dataA = memory.Read16(a & ChipAddressMask);
                    a = (uint)(a + step);
                }

                if ((control0 & UseB) != 0)
                {
                    dataB = memory.Read16(b & ChipAddressMask);
                    b = (uint)(b + step);
                }

                if ((control0 & UseC) != 0)
                {
                    dataC = memory.Read16(c & ChipAddressMask);
                    c = (uint)(c + step);
                }

                var maskedA = dataA;
                if (column == 0)
                    maskedA &= custom[BlitterRegister.Bltafwm];
                if (column == width - 1)
                    maskedA &= custom[BlitterRegister.Bltalwm];

                var shiftedA = Shift(maskedA, previousA, shiftA, descending);
                var shiftedB = Shift(dataB, previousB, shiftB, descending);
                previousA = maskedA;
                previousB = dataB;

                var result = Minterm(minterm, shiftedA, shiftedB, dataC);
                if (fill)
                    result = Fill(result, (control1 & ExclusiveFill) != 0, ref carry);
                if (result != 0)
                    zero = false;

                if ((control0 & UseD) != 0)
                {
                    memory.Write16(d & ChipAddressMask, result);
                    d = (uint)(d + step);
                }
            }

            if ((control0 & UseA) != 0)
                a = (uint)(a + moduloA);
            if ((control0 & UseB) != 0)
                b = (uint)(b + moduloB);
            if ((control0 & UseC) != 0)
                c = (uint)(c + moduloC);
            if ((control0 & UseD) != 0)
                d = (uint)(d + moduloD);
        }

        SetPointer(BlitterRegister.Bltapt, a);
        SetPointer(BlitterRegister.Bltbpt, b);
        SetPointer(BlitterRegister.Bltcpt, c);
        SetPointer(BlitterRegister.Bltdpt, d);
        custom.BlitterZero = zero;
    }

    private void Line(int length)
    {
        var control0 = custom[BlitterRegister.Bltcon0];
        var control1 = custom[BlitterRegister.Bltcon1];
        var minterm = (byte)control0;
        var bit = control0 >> 12;
        var textureBit = control1 >> 12;
        var texture = custom[BlitterRegister.Bltbdat];
        var dataA = custom[BlitterRegister.Bltadat];
        var rowBytes = (short)custom[BlitterRegister.Bltcmod];
        var error = (short)custom[BlitterRegister.Bltapt + 2];
        var errorIfPositive = (short)custom[BlitterRegister.Bltamod];
        var errorIfNegative = (short)custom[BlitterRegister.Bltbmod];
        var negative = (control1 & Sign) != 0;
        var pointer = Pointer(BlitterRegister.Bltcpt);
        var target = Pointer(BlitterRegister.Bltdpt);
        var dotInRow = false;
        var zero = true;

        for (var i = 0; i < length; i++)
        {
            var dataC = memory.Read16(pointer & ChipAddressMask);
            var a = (ushort)(dataA >> bit);
            var b = (texture >> (15 - textureBit) & 1) != 0 ? (ushort)0xFFFF : (ushort)0;
            var result = Minterm(minterm, a, b, dataC);
            // In one-dot mode, only the first pixel of each row changes the memory.
            if ((control1 & SingleDot) != 0 && dotInRow)
                result = dataC;
            dotInRow = true;
            if (result != 0)
                zero = false;
            if ((control0 & UseD) != 0)
                memory.Write16(target & ChipAddressMask, result);

            // The major axis takes a step each time. The minor axis takes a step while the error is not negative.
            error += negative ? errorIfNegative : errorIfPositive;
            var vertical = (control1 & StepVertical) != 0;
            StepAxis(vertical, (control1 & StepUpOrLeft) != 0);
            if (!negative)
                StepAxis(!vertical, (control1 & AlternateUpOrLeft) != 0);
            negative = error < 0;
            textureBit = (textureBit + 1) & 15;
            target = pointer;
        }

        custom.Write(BlitterRegister.Bltapt + 2, (ushort)error);
        SetPointer(BlitterRegister.Bltcpt, pointer);
        SetPointer(BlitterRegister.Bltdpt, pointer);
        custom.BlitterZero = zero;
        return;

        void StepAxis(bool vertical, bool backwards)
        {
            if (vertical)
            {
                pointer = (uint)(pointer + (backwards ? -rowBytes : rowBytes));
                dotInRow = false;
            }
            else if (backwards)
            {
                if (bit == 0)
                    pointer -= 2;
                bit = (bit - 1) & 15;
            }
            else
            {
                bit = (bit + 1) & 15;
                if (bit == 0)
                    pointer += 2;
            }
        }
    }

    /// <summary>
    /// Shifts a word right (left in descending mode) by the shift count. The bits that come in are from the previous
    /// word of the channel.
    /// </summary>
    private static ushort Shift(ushort value, int previous, int shift, bool descending) =>
        descending
            ? (ushort)((((uint)value << 16 | (uint)previous) << shift) >> 16)
            : (ushort)(((uint)previous << 16 | value) >> shift);

    /// <summary>
    /// The logic function of BLTCON0 bits 7 to 0. Each bit gives the result for one combination of A, B and C: bit 7
    /// for ABC, bit 6 for ABc, and so on to bit 0 for abc.
    /// </summary>
    public static ushort Minterm(byte function, ushort a, ushort b, ushort c)
    {
        var result = 0;
        if ((function & 0x80) != 0) result |= a & b & c;
        if ((function & 0x40) != 0) result |= a & b & ~c;
        if ((function & 0x20) != 0) result |= a & ~b & c;
        if ((function & 0x10) != 0) result |= a & ~b & ~c;
        if ((function & 0x08) != 0) result |= ~a & b & c;
        if ((function & 0x04) != 0) result |= ~a & b & ~c;
        if ((function & 0x02) != 0) result |= ~a & ~b & c;
        if ((function & 0x01) != 0) result |= ~a & ~b & ~c;
        return (ushort)result;
    }

    /// <summary>
    /// Fills a word from bit 0 to bit 15. A set bit toggles the carry. The inclusive fill keeps the set bits, and the
    /// exclusive fill gives the carry after the toggle, so it clears the left bit of each pair.
    /// </summary>
    private static ushort Fill(ushort value, bool exclusive, ref bool carry)
    {
        var result = 0;
        for (var i = 0; i < 16; i++)
        {
            var set = (value & (1 << i)) != 0;
            if (set)
                carry = !carry;
            if (carry || (set && !exclusive))
                result |= 1 << i;
        }

        return (ushort)result;
    }

    private uint Pointer(int register) => (uint)(custom[register] << 16 | custom[register + 2]);

    private void SetPointer(int register, uint value)
    {
        custom.Write(register, (ushort)(value >> 16));
        custom.Write(register + 2, (ushort)value);
    }

    /// <summary>A modulo is a signed byte count, even. In descending mode, the pointer goes down by the modulo.</summary>
    private int Modulo(int register, bool descending)
    {
        var value = (short)custom[register] & ~1;
        return descending ? -value : value;
    }
}
