namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// Makes the picture of each frame from the custom chip registers, as Denise shows it. At the end of each frame the
/// copper runs from COP1LC, line by line. The bitplanes of each line are fetched, and each pixel gets its color. A
/// register write of the copper changes the pixels from its horizontal position.
/// </summary>
/// <remarks>
/// The picture is 768 by 480 pixels in high resolution: lines 21 to 260 of NTSC, two rows for each line. An interlaced
/// frame fills one row of the two: the long frame fills the even rows. The display shows the bitplanes, the display
/// window, the scroll, dual playfield and extra half-brite. It does not show sprites or HAM.
/// </remarks>
public sealed class Display
{
    public const int Width = 768;
    public const int Height = 480;

    /// <summary>The first line in the picture.</summary>
    public const int FirstLine = 21;

    /// <summary>The low-resolution pixel position of the left edge of the picture.</summary>
    public const int FirstLowResolutionPixel = 0x48;

    // BPLCON0 bits.
    private const int HighResolution = 0x8000;
    private const int HoldAndModify = 0x0800;
    private const int DualPlayfield = 0x0400;
    private const int Interlace = 0x0004;

    // DMACON bits.
    private const int DmaEnable = 0x0200;
    private const int BitplaneDma = 0x0100;
    private const int CopperDma = 0x0080;

    private readonly Memory _memory;
    private readonly CustomChips _custom;
    private readonly Copper _copper;
    private readonly List<CopperWrite> _writes = [];
    private readonly ushort[] _state = new ushort[0x100];
    private readonly byte[][] _planeData = new byte[6][];
    private readonly object _frameLock = new();
    // The display draws into the canvas. With interlace, a frame changes only its rows, and the rows of the other
    // frame stay. The host reads the last complete picture from _front.
    private readonly uint[] _canvas = new uint[Width * Height];
    private readonly uint[] _front = new uint[Width * Height];

    // After the fetch of a line, each plane pointer moves to the next line of its plane. The display adds the
    // modulo when the last fetch of the line starts, at DDFSTOP. A copper write after that sets the pointer for the
    // next line as written.
    private bool _moduloPending;
    private int _moduloPosition;
    private int _fetchedPlanes;
    private int _fetchedWords;

    // The address where the copper starts in the next frame. The copper starts at COP1LC at the start of the frame,
    // before the program handles the vertical blank. A change of COP1LC in the handler is for the frame after that.
    private uint? _frameStart;

    public Display(Memory memory, CustomChips custom)
    {
        _memory = memory;
        _custom = custom;
        _copper = new Copper(memory, custom);
        for (var plane = 0; plane < _planeData.Length; plane++)
            _planeData[plane] = new byte[256];
    }

    /// <summary>The number of frames that the display made. The host shows a new frame when it changes.</summary>
    public long FrameNumber { get; private set; }

    /// <summary>
    /// Set a writer to dump the next frame that the host shows. The dump has the registers at the start of the frame
    /// and each copper write with its line and horizontal position. The display then sets the property to null.
    /// </summary>
    public TextWriter? CopperDump { get; set; }

    /// <summary>The host time that the display used to make the frames.</summary>
    public TimeSpan RenderTime { get; private set; }

    /// <summary>True if the last frame used interlace.</summary>
    public bool IsInterlaced { get; private set; }

    /// <summary>Copies the last complete picture, as 0xAARRGGBB pixels.</summary>
    public void CopyFrame(uint[] target)
    {
        lock (_frameLock)
            Array.Copy(_front, target, Width * Height);
    }

    /// <summary>
    /// The program wrote to COPJMP1 or COPJMP2. The copper starts again at COP1LC or COP2LC in the current frame.
    /// </summary>
    public void CopperJumped(int list) =>
        _frameStart = Location(list == 1 ? CustomRegister.Cop1lc : CustomRegister.Cop2lc);

    private uint Location(int offset) => (uint)(_custom[offset] << 16 | _custom[offset + 2]);

    /// <summary>Runs the copper and makes the picture of one frame.</summary>
    /// <param name="longFrame">True for the long frame of an interlaced display, which has 263 lines.</param>
    /// <param name="render">False to run only the copper, for a frame that the host does not show.</param>
    public void RunFrame(bool longFrame, bool render)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            MakeFrame(longFrame, render);
        }
        finally
        {
            RenderTime += System.Diagnostics.Stopwatch.GetElapsedTime(start);
        }
    }

    private void MakeFrame(bool longFrame, bool render)
    {
        // The display keeps its own copy of the registers, because the copper writes change the pixels from their
        // position in the line.
        for (var i = 0; i < _state.Length; i++)
            _state[i] = _custom[i * 2];

        var dmacon = _custom.Dmacon;
        var copper = (dmacon & (DmaEnable | CopperDma)) == (DmaEnable | CopperDma);
        if (copper)
            _copper.Start(_frameStart ?? Location(CustomRegister.Cop1lc));
        else
            _copper.Stop();

        var dump = render ? CopperDump : null;
        dump?.WriteLine($"frame {FrameNumber + 1}, {(longFrame ? "long" : "short")}, copper at ${(_frameStart ?? Location(CustomRegister.Cop1lc)):X6}");
        if (dump != null)
        {
            for (var offset = 0x080; offset < 0x1C0; offset += 2)
                dump.WriteLine($"  start {offset:X3} = {_state[offset >> 1]:X4}");
        }

        var interlaced = (_state[CustomRegister.Bplcon0 >> 1] & Interlace) != 0;
        var lines = interlaced && longFrame ? 263 : 262;
        for (var line = 0; line < lines; line++)
        {
            _writes.Clear();
            if (copper)
                _copper.RunLine(line, _writes);
            if (dump != null)
            {
                foreach (var write in _writes)
                    dump.WriteLine($"  line {line,3} h {write.Horizontal:X2}: {write.Offset:X3} = {write.Value:X4}");
            }

            RenderLine(line, render, longFrame);
        }

        if (dump != null)
        {
            dump.Flush();
            CopperDump = null;
        }

        IsInterlaced = (_state[CustomRegister.Bplcon0 >> 1] & Interlace) != 0;
        _frameStart = Location(CustomRegister.Cop1lc);
        if (!render)
            return;
        lock (_frameLock)
            Array.Copy(_canvas, _front, _canvas.Length);
        FrameNumber++;
    }

    private void RenderLine(int line, bool render, bool longFrame)
    {
        var next = 0;

        // The writes before the data fetch starts change the fetch of this line.
        ApplyWrites(ref next, 0x18);

        var bplcon0 = State(CustomRegister.Bplcon0);
        var highResolution = (bplcon0 & HighResolution) != 0;
        var planes = Math.Min((bplcon0 >> 12) & 7, 6);
        var diwStart = State(CustomRegister.Diwstrt);
        var diwStop = State(CustomRegister.Diwstop);
        var verticalStart = diwStart >> 8;
        // The stop line has a ninth bit that is the inverse of bit 7.
        var verticalStop = (diwStop >> 8) | ((diwStop & 0x8000) == 0 ? 0x100 : 0);
        var horizontalStart = diwStart & 0xFF;
        var horizontalStop = (diwStop & 0xFF) | 0x100;

        var dmacon = _custom.Dmacon;
        var active = line >= verticalStart && line < verticalStop && planes > 0
                     && (dmacon & (DmaEnable | BitplaneDma)) == (DmaEnable | BitplaneDma);

        var fetchStart = State(CustomRegister.Ddfstrt) & 0xFC;
        var fetchStop = State(CustomRegister.Ddfstop) & 0xFC;
        var words = highResolution
            ? Math.Max((fetchStop - fetchStart) / 4 + 2, 0)
            : Math.Max((fetchStop - fetchStart) / 8 + 1, 0);
        words = Math.Min(words, 128);
        if (active)
        {
            for (var plane = 0; plane < planes; plane++)
                _memory.Ram(PlanePointer(plane), words * 2).CopyTo(_planeData[plane]);
            _moduloPending = true;
            _moduloPosition = fetchStop;
            _fetchedPlanes = planes;
            _fetchedWords = words;
        }

        if (render && line >= FirstLine && line < FirstLine + Height / 2)
            DrawPixels(line, longFrame, ref next, active, planes, highResolution, fetchStart, words,
                horizontalStart, horizontalStop);

        ApplyWrites(ref next, int.MaxValue);
    }

    /// <summary>
    /// Moves each fetched plane pointer to the next line of its plane: the bytes of the line and the modulo. The odd
    /// planes use BPL1MOD and the even planes use BPL2MOD.
    /// </summary>
    private void AddModulo()
    {
        _moduloPending = false;
        for (var plane = 0; plane < _fetchedPlanes; plane++)
        {
            var modulo = (short)State(plane % 2 == 0 ? CustomRegister.Bpl1mod : CustomRegister.Bpl2mod);
            SetPlanePointer(plane, (uint)(PlanePointer(plane) + _fetchedWords * 2 + modulo));
        }
    }

    private void DrawPixels(int line, bool longFrame, ref int next, bool active, int planes, bool highResolution,
        int fetchStart, int words, int horizontalStart, int horizontalStop)
    {
        var row = (line - FirstLine) * 2;
        var interlaced = (State(CustomRegister.Bplcon0) & Interlace) != 0;
        if (interlaced && !longFrame)
            row++;

        // The first fetched pixel appears 17 low-resolution pixels (9 in high resolution) after twice DDFSTRT.
        var firstDataPixel = (fetchStart * 2 + (highResolution ? 9 : 17) - FirstLowResolutionPixel) * 2;
        var dataPixels = words * 16 * (highResolution ? 1 : 2);
        var bplcon0 = State(CustomRegister.Bplcon0);
        var dualPlayfield = (bplcon0 & DualPlayfield) != 0;
        var extraHalfBrite = planes == 6 && (bplcon0 & (HoldAndModify | DualPlayfield)) == 0;
        var target = _canvas.AsSpan(row * Width, Width);

        for (var x = 0; x < Width; x++)
        {
            var lowResolution = FirstLowResolutionPixel + x / 2;
            ApplyWrites(ref next, lowResolution / 2);

            var index = 0;
            if (active && lowResolution >= horizontalStart && lowResolution < horizontalStop)
            {
                var bplcon1 = State(CustomRegister.Bplcon1);
                for (var plane = 0; plane < planes; plane++)
                {
                    // The scroll delay is in low-resolution pixels: odd planes use bits 3 to 0, even planes bits 7 to 4.
                    var delay = (plane % 2 == 0 ? bplcon1 : bplcon1 >> 4) & 0xF;
                    var pixel = x - firstDataPixel - delay * 2;
                    if (pixel < 0 || pixel >= dataPixels)
                        continue;
                    var bit = highResolution ? pixel : pixel / 2;
                    if ((_planeData[plane][bit >> 3] & (0x80 >> (bit & 7))) != 0)
                        index |= 1 << plane;
                }
            }

            target[x] = Color(index, dualPlayfield, extraHalfBrite);
        }

        if (!interlaced)
            target.CopyTo(_canvas.AsSpan((row + 1) * Width, Width));
    }

    /// <summary>The color of a pixel from the bits of its planes.</summary>
    private uint Color(int index, bool dualPlayfield, bool extraHalfBrite)
    {
        if (dualPlayfield)
        {
            // Playfield 1 has the odd planes (colors 0 to 7), and playfield 2 has the even planes (colors 8 to 15).
            var playfield1 = (index & 1) | ((index >> 1) & 2) | ((index >> 2) & 4);
            var playfield2 = ((index >> 1) & 1) | ((index >> 2) & 2) | ((index >> 3) & 4);
            var playfield2First = (State(CustomRegister.Bplcon2) & 0x40) != 0;
            if (playfield2First)
                index = playfield2 != 0 ? playfield2 + 8 : playfield1;
            else
                index = playfield1 != 0 ? playfield1 : playfield2 != 0 ? playfield2 + 8 : 0;
        }

        var halfBrite = extraHalfBrite && index >= 32;
        var rgb = State(CustomRegister.Color00 + (index & 31) * 2);
        int red = (rgb >> 8) & 0xF, green = (rgb >> 4) & 0xF, blue = rgb & 0xF;
        if (halfBrite)
        {
            red >>= 1;
            green >>= 1;
            blue >>= 1;
        }

        return 0xFF00_0000u | (uint)(red * 17) << 16 | (uint)(green * 17) << 8 | (uint)(blue * 17);
    }

    /// <summary>Applies the copper writes of this line up to the horizontal position.</summary>
    private void ApplyWrites(ref int next, int horizontal)
    {
        while (next < _writes.Count && _writes[next].Horizontal <= horizontal)
        {
            var write = _writes[next++];
            if (_moduloPending && write.Horizontal >= _moduloPosition)
                AddModulo();
            _state[write.Offset >> 1] = write.Value;
        }

        if (_moduloPending && horizontal >= _moduloPosition)
            AddModulo();
    }

    private ushort State(int offset) => _state[offset >> 1];

    private uint PlanePointer(int plane)
    {
        var offset = CustomRegister.Bpl1pt + plane * 4;
        return (uint)(State(offset) << 16 | State(offset + 2)) & 0x1F_FFFE;
    }

    private void SetPlanePointer(int plane, uint value)
    {
        var offset = CustomRegister.Bpl1pt + plane * 4;
        _state[offset >> 1] = (ushort)(value >> 16);
        _state[(offset + 2) >> 1] = (ushort)value;
    }
}
