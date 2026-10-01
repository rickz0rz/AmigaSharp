namespace AmigaSharp.Runtime.Hardware;

/// <summary>How the host shows the two fields of an interlaced display.</summary>
public enum DeinterlaceMode
{
    /// <summary>Both fields, each on its rows, as a TV shows them. Moving content has comb lines.</summary>
    Weave,

    /// <summary>The last field only, each row twice. No comb lines, and half the vertical detail.</summary>
    Bob,

    /// <summary>The average of the two fields. No comb lines, and moving content is a little blurred.</summary>
    Blend,
}

/// <summary>
/// Makes the picture of each frame from the custom chip registers, as Denise shows it. The copper starts at COP1LC at
/// the start of each frame. The display makes each line when the beam passes it (at the next safe point): the copper
/// runs for the line, the bitplanes of the line are fetched, and each pixel gets its color. A register write of the
/// copper changes the pixels from its horizontal position, and a write of the CPU changes the lines after the beam.
/// </summary>
/// <remarks>
/// <para>
/// The picture is 768 pixels wide in high resolution, with two rows for each line: 480 rows for lines 21 to 260 of
/// NTSC, and 572 rows for lines 26 to 311 of PAL. An interlaced frame fills one row of the two: the long frame fills
/// the even rows. The display shows the bitplanes, the display
/// window, the scroll, dual playfield, extra half-brite and sprites. It does not show HAM.
/// </para>
/// <para>
/// A pixel that is the genlock key has alpha 0, and the other pixels have alpha $FF. A genlock shows its video in the
/// key pixels. See <see cref="IsGenlockKey"/>.
/// </para>
/// </remarks>
public sealed class Display
{
    public const int Width = 768;

    /// <summary>The number of rows of the picture: two for each line. It depends on the video standard.</summary>
    public int Height { get; }

    /// <summary>The first line in the picture.</summary>
    public int FirstLine { get; }

    /// <summary>The number of rows of the picture for a video standard: 480 for NTSC, and 572 for PAL.</summary>
    public static int HeightOf(VideoStandard standard) => standard.PictureLines * 2;

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
    private const int SpriteDma = 0x0020;

    // Genlock bits: BPLCON0 ECSENA, BPLCON2 ZDBPEN and ZDCTEN, BPLCON3 BRDNTRAN.
    private const int EcsEnable = 0x0001;
    private const int KeyBitplaneEnable = 0x0800;
    private const int KeyColorTableEnable = 0x0400;
    private const int Bplcon3 = 0x106;
    private const int BorderNotTransparent = 0x0010;
    private const int BorderBlank = 0x0020;
    private const int Bplcon4 = 0x10C;
    private const int Fmode = 0x1FC;

    // Sprite registers.
    private const int Spr0pt = 0x120;
    private const int Spr0pos = 0x140;

    /// <summary>The first line where sprite DMA fetches: the end of the vertical blank.</summary>
    private const int FirstSpriteLine = 20;

    private readonly Memory _memory;
    private readonly CustomChips _custom;
    private readonly Copper _copper;
    private readonly List<CopperWrite> _writes = [];
    private readonly ushort[] _state = new ushort[0x100];
    private readonly byte[][] _planeData = new byte[8][];
    private readonly object _frameLock = new();
    // The display draws into the canvas. With interlace, a frame changes only its rows, and the rows of the other
    // frame stay. The host reads the last complete picture from _front.
    private readonly uint[] _canvas;
    private readonly uint[] _front;
    private bool _frontInterlaced;
    private bool _frontOddField;

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

    // The frame that the display makes now. The display makes each line when the beam passes it, so a line shows the
    // memory and the registers at that time, as on the hardware. The frame starts when the first line is due.
    private bool _frameActive;
    private long _frameOfBeam;
    private bool _longFrame;
    private bool _copperOn;
    private int _lines;
    private int _nextLine;
    private TextWriter? _dump;
    private bool _runningCopper;
    private readonly Beam? _beam;
    private readonly VideoStandard _standard;

    // With AGA, the colors come from the palette of 256 colors, in the order of the register writes of the frame.
    private readonly bool _aga;
    private readonly uint[] _palette = new uint[256];
    private readonly Sprite[] _sprites = new Sprite[8];

    // The width of the sprites in pixels: 16, or 32 and 64 with the sprite bits of FMODE (AGA).
    private int _spriteWidth = 16;

    // The line that the display makes now, and true when its sprite DMA ran.
    private int _line;
    private bool _spritesFetched;

    // True when a sprite can show on the line that the display draws now.
    private bool _spritesOnLine;

    /// <summary>The state of a sprite DMA channel in the current frame.</summary>
    private struct Sprite
    {
        public bool Loaded;
        public bool Armed;
        public int VerticalStart;
        public int VerticalStop;
        public int HorizontalStart;
        public bool Attached;
        // The data of the line, as many bits as the sprite width, with the first pixel in the high bit.
        public ulong DataA;
        public ulong DataB;
    }

    /// <param name="beam">The beam position. Without it, the display makes each frame at its end.</param>
    public Display(Memory memory, CustomChips custom, Beam? beam = null)
    {
        _memory = memory;
        _custom = custom;
        _beam = beam;
        _standard = beam?.Standard ?? VideoStandard.Ntsc;
        _aga = custom.Aga;
        Height = HeightOf(_standard);
        FirstLine = _standard.FirstLine;
        _canvas = new uint[Width * Height];
        _front = new uint[Width * Height];
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

    /// <summary>How <see cref="CopyFrame"/> shows an interlaced display.</summary>
    public DeinterlaceMode Deinterlace { get; set; } = DeinterlaceMode.Weave;

    /// <summary>Copies the last complete picture, as 0xAARRGGBB pixels.</summary>
    public void CopyFrame(uint[] target)
    {
        lock (_frameLock)
        {
            Array.Copy(_front, target, Width * Height);
            if (!_frontInterlaced)
                return;

            for (var row = 0; row < Height; row += 2)
            {
                var even = target.AsSpan(row * Width, Width);
                var odd = target.AsSpan((row + 1) * Width, Width);
                switch (Deinterlace)
                {
                    case DeinterlaceMode.Bob when _frontOddField:
                        odd.CopyTo(even);
                        break;
                    case DeinterlaceMode.Bob:
                        even.CopyTo(odd);
                        break;
                    case DeinterlaceMode.Blend:
                        for (var x = 0; x < Width; x++)
                            even[x] = odd[x] = Average(even[x], odd[x]);
                        break;
                }
            }
        }
    }

    // The alpha channel is averaged too: a pixel that is the genlock key in one field only is half transparent.
    private static uint Average(uint a, uint b) =>
        ((a & 0xFEFEFEFE) >> 1) + ((b & 0xFEFEFEFE) >> 1) + (a & b & 0x01010101);

    /// <summary>
    /// The program wrote to COPJMP1 or COPJMP2. The copper starts again at COP1LC or COP2LC in the current frame.
    /// </summary>
    public void CopperJumped(int list)
    {
        // The copper does its own COPJMP writes.
        if (_runningCopper)
            return;
        var location = Location(list == 1 ? CustomRegister.Cop1lc : CustomRegister.Cop2lc);
        if (!_frameActive)
        {
            _frameStart = location;
            return;
        }

        // The copper starts again at the current line.
        CatchUp();
        if (_copperOn)
            _copper.Start(location);
    }

    /// <summary>
    /// A register changed. A write of the CPU changes the lines after the beam: the display first makes the lines
    /// before it. The copper writes go to the display in their lines.
    /// </summary>
    public void RegisterWritten(int offset, ushort value)
    {
        if (_runningCopper || !_frameActive)
            return;
        CatchUp();
        SetRegister(offset, value);
    }

    /// <summary>Makes the lines of the current frame up to the line of the beam.</summary>
    public void CatchUp()
    {
        if (_beam == null)
            return;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!_frameActive)
            {
                // The first frame starts when its first line is due.
                if (_beam.Line == 0)
                    return;
                BeginFrame(_custom.LongFrame);
            }

            RunLines(_beam.Frame > _frameOfBeam ? _lines : Math.Min(_beam.Line, _lines));
        }
        finally
        {
            RenderTime += System.Diagnostics.Stopwatch.GetElapsedTime(start);
        }
    }

    private uint Location(int offset) => (uint)(_custom[offset] << 16 | _custom[offset + 2]);

    /// <summary>Runs the copper and makes the picture of one frame.</summary>
    /// <param name="longFrame">True for the long frame of an interlaced display, which has one more line.</param>
    /// <param name="render">False to run only the copper, for a frame that the host does not show.</param>
    public void RunFrame(bool longFrame, bool render)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!_frameActive)
                BeginFrame(longFrame);
            RunLines(_lines);
            EndFrame(render);
        }
        finally
        {
            RenderTime += System.Diagnostics.Stopwatch.GetElapsedTime(start);
        }
    }

    /// <summary>A new frame started. The display makes its lines when the beam passes them.</summary>
    public void FrameStarted(bool longFrame)
    {
        if (_beam != null)
            BeginFrame(longFrame);
    }

    private void BeginFrame(bool longFrame)
    {
        // The display keeps its own copy of the registers, because the copper writes change the pixels from their
        // position in the line.
        for (var i = 0; i < _state.Length; i++)
            _state[i] = _custom[i * 2];
        if (_aga)
            Array.Copy(_custom.Palette, _palette, _palette.Length);

        var dmacon = _custom.Dmacon;
        _copperOn = (dmacon & (DmaEnable | CopperDma)) == (DmaEnable | CopperDma);
        if (_copperOn)
            _copper.Start(_frameStart ?? Location(CustomRegister.Cop1lc));
        else
            _copper.Stop();

        // A frame that the host does not show is not in the dump. The display knows that only at the end, so the dump
        // starts with the next frame after the request.
        _dump = CopperDump;
        _dump?.WriteLine($"frame {FrameNumber + 1}, {(longFrame ? "long" : "short")}, copper at ${(_frameStart ?? Location(CustomRegister.Cop1lc)):X6}");
        if (_dump != null)
        {
            for (var offset = 0x080; offset < 0x200; offset += 2)
                _dump.WriteLine($"  start {offset:X3} = {_state[offset >> 1]:X4}");
        }

        var interlaced = (_state[CustomRegister.Bplcon0 >> 1] & Interlace) != 0;
        _lines = interlaced && longFrame ? _standard.LinesPerFrame + 1 : _standard.LinesPerFrame;
        _longFrame = longFrame;
        _nextLine = 0;
        Array.Clear(_sprites);
        _frameOfBeam = _beam?.Frame ?? 0;
        _frameActive = true;
    }

    private void RunLines(int end)
    {
        for (; _nextLine < end; _nextLine++)
        {
            var line = _nextLine;
            _writes.Clear();
            if (_copperOn)
            {
                _runningCopper = true;
                try
                {
                    _copper.RunLine(line, _writes);
                }
                finally
                {
                    _runningCopper = false;
                }
            }

            if (_dump != null)
            {
                foreach (var write in _writes)
                    _dump.WriteLine($"  line {line,3} h {write.Horizontal:X2}: {write.Offset:X3} = {write.Value:X4}");
            }

            RenderLine(line, render: true, _longFrame);
        }
    }

    private void EndFrame(bool render)
    {
        var longFrame = _longFrame;
        _frameActive = false;
        if (_dump != null)
        {
            _dump.Flush();
            _dump = null;
            CopperDump = null;
        }

        IsInterlaced = (_state[CustomRegister.Bplcon0 >> 1] & Interlace) != 0;
        _frameStart = Location(CustomRegister.Cop1lc);
        if (!render)
            return;
        lock (_frameLock)
        {
            Array.Copy(_canvas, _front, _canvas.Length);
            _frontInterlaced = IsInterlaced;
            // The short frame fills the odd rows.
            _frontOddField = !longFrame;
        }

        FrameNumber++;
    }

    private void RenderLine(int line, bool render, bool longFrame)
    {
        var next = 0;
        _line = line;
        _spritesFetched = false;

        // The writes before the data fetch starts change the fetch of this line.
        ApplyWrites(ref next, 0x18);
        FetchSprites(line);

        var bplcon0 = State(CustomRegister.Bplcon0);
        var highResolution = (bplcon0 & HighResolution) != 0;
        var planes = Math.Min((bplcon0 >> 12) & 7, 6);
        // AGA has up to 8 planes: BPU3 (bit 4) adds 8 to the number of planes.
        if (_aga)
            planes = Math.Min(((bplcon0 >> 12) & 7) | ((bplcon0 & 0x0010) != 0 ? 8 : 0), 8);
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
        int fetchStop, words, latency;
        var fetchFactor = _aga ? FetchFactor(State(Fmode)) : 1;
        if (fetchFactor == 1)
        {
            fetchStop = FetchStop(fetchStart, State(CustomRegister.Ddfstop) & 0xFE, highResolution ? 4 : 8);
            words = highResolution
                ? Math.Max((fetchStop - fetchStart) / 4 + 2, 0)
                : Math.Max((fetchStop - fetchStart) / 8 + 1, 0);
            latency = highResolution ? 9 : 17;
        }
        else
        {
            // The fetch modes of AGA fetch 2 or 4 words of each plane in a unit that is 2 or 4 times as long. The units
            // start at DDFSTRT, and the last unit starts at DDFSTOP or before it.
            var unit = (highResolution ? 4 : 8) * fetchFactor;
            var stop = Math.Min(State(CustomRegister.Ddfstop) & 0xFE, HardwareFetchStop);
            var units = stop >= fetchStart ? (stop - fetchStart) / unit + 1 : 0;
            fetchStop = fetchStart + Math.Max(units - 1, 0) * unit;
            words = units * fetchFactor;
            // The data of a unit shows after the unit, as with one word: 17 pixels for low resolution.
            latency = unit * 2 + 1;
        }

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
            DrawPixels(line, longFrame, ref next, active, planes, highResolution, fetchStart, words, latency,
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

    /// <summary>The words of each plane in a fetch unit, from the bitplane bits of FMODE: 1, 2, 2 or 4.</summary>
    private static int FetchFactor(ushort fmode) => (fmode & 3) switch
    {
        0 => 1,
        3 => 4,
        _ => 2,
    };

    /// <summary>The last position where Agnus can start a fetch unit.</summary>
    private const int HardwareFetchStop = 0xD8;

    /// <summary>
    /// The position of the last fetch unit of a line. A fetch unit starts each 4 color clocks in high resolution and
    /// each 8 in low resolution, from DDFSTRT. Agnus compares the start of each unit with DDFSTOP, and the fetch stops
    /// after the unit that is equal to it. If no unit is equal to DDFSTOP, the fetch continues to the hardware limit
    /// at $D8. For example, Prevue uses DDFSTRT $28 and DDFSTOP $D6 in high resolution, and gets 46 words.
    /// </summary>
    private static int FetchStop(int start, int stop, int unit)
    {
        if (stop >= start && stop <= HardwareFetchStop && (stop - start) % unit == 0)
            return stop;
        return start + Math.Max(HardwareFetchStop - start, 0) / unit * unit;
    }

    /// <param name="latency">The low-resolution pixels from twice the start of a fetch unit to its first pixel.</param>
    private void DrawPixels(int line, bool longFrame, ref int next, bool active, int planes, bool highResolution,
        int fetchStart, int words, int latency, int horizontalStart, int horizontalStop)
    {
        var row = (line - FirstLine) * 2;
        var interlaced = (State(CustomRegister.Bplcon0) & Interlace) != 0;
        if (interlaced && !longFrame)
            row++;

        // The first fetched pixel appears 17 low-resolution pixels (9 in high resolution) after twice DDFSTRT, or later
        // with the fetch modes of AGA.
        var firstDataPixel = (fetchStart * 2 + latency - FirstLowResolutionPixel) * 2;
        var dataPixels = words * 16 * (highResolution ? 1 : 2);
        var bplcon0 = State(CustomRegister.Bplcon0);
        var dualPlayfield = (bplcon0 & DualPlayfield) != 0;
        var extraHalfBrite = planes == 6 && (bplcon0 & (HoldAndModify | DualPlayfield)) == 0;
        var target = _canvas.AsSpan(row * Width, Width);
        _spritesOnLine = false;
        foreach (var sprite in _sprites)
            _spritesOnLine |= sprite.Armed && (sprite.DataA | sprite.DataB) != 0;
        var insideVertically = line >= (State(CustomRegister.Diwstrt) >> 8)
                               && line < ((State(CustomRegister.Diwstop) >> 8) | ((State(CustomRegister.Diwstop) & 0x8000) == 0 ? 0x100 : 0));

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
                    // AGA adds PF1H6-7 (bits 10 and 11) and PF2H6-7 (bits 14 and 15): 16 to 48 more pixels.
                    if (_aga)
                        delay |= ((plane % 2 == 0 ? bplcon1 >> 10 : bplcon1 >> 14) & 3) << 4;
                    var pixel = x - firstDataPixel - delay * 2;
                    if (pixel < 0 || pixel >= dataPixels)
                        continue;
                    var bit = highResolution ? pixel : pixel / 2;
                    if ((_planeData[plane][bit >> 3] & (0x80 >> (bit & 7))) != 0)
                        index |= 1 << plane;
                }
            }

            var sprite = _spritesOnLine && insideVertically && lowResolution >= horizontalStart && lowResolution < horizontalStop
                ? SpritePixel(lowResolution, index, dualPlayfield)
                : 0;
            var border = !insideVertically || lowResolution < horizontalStart || lowResolution >= horizontalStop;
            target[x] = sprite != 0
                ? SpriteColor(sprite)
                : PlayfieldPixel(index, dualPlayfield, extraHalfBrite, border);
        }

        if (!interlaced)
            target.CopyTo(_canvas.AsSpan((row + 1) * Width, Width));
    }

    /// <summary>
    /// The color of a sprite pixel from its color register (17 to 31). With AGA, the sprites use the bank of 16 colors
    /// that ESPRM (BPLCON4 bits 7 to 4) gives. OSPRM of the odd sprites is not emulated: they use ESPRM too.
    /// </summary>
    private uint SpriteColor(int register)
    {
        if (!_aga)
            return Rgb(State(CustomRegister.Color00 + register * 2), false);
        var bank = (State(Bplcon4) >> 4) & 0xF;
        return 0xFF00_0000 | _palette[bank * 16 + register - 16];
    }

    /// <summary>
    /// The color of a playfield pixel. A pixel that is the genlock key has alpha 0: a genlock shows its video there. The
    /// other pixels have alpha $FF.
    /// </summary>
    private uint PlayfieldPixel(int planes, bool dualPlayfield, bool extraHalfBrite, bool border)
    {
        var register = ColorRegister(planes, dualPlayfield);
        uint color;
        if (border && (State(CustomRegister.Bplcon0) & EcsEnable) != 0 && (State(Bplcon3) & BorderBlank) != 0)
            color = 0xFF00_0000;
        else if (_aga)
            // BPLAM (BPLCON4 bits 15 to 8) changes the color number of each playfield pixel.
            color = 0xFF00_0000 | _palette[(register ^ (State(Bplcon4) >> 8)) & 0xFF];
        else
            color = Rgb(State(CustomRegister.Color00 + (register & 31) * 2), extraHalfBrite && register >= 32);
        return IsGenlockKey(planes, register, border) ? color & 0x00FF_FFFF : color;
    }

    /// <summary>
    /// True if the pixel is the genlock key (the ZD pin of Denise). With ZDBPEN (BPLCON2 bit 11), the key is the
    /// bitplane that ZDBPSEL (bits 14 to 12) selects. With ZDCTEN (bit 10), it is bit 15 of the color register of the
    /// pixel. Else it is color 0. The border is the key, except with BRDNTRAN (BPLCON3 bit 4) of the ECS Denise.
    /// </summary>
    private bool IsGenlockKey(int planes, int register, bool border)
    {
        if (border)
        {
            var ecs = (State(CustomRegister.Bplcon0) & EcsEnable) != 0;
            return !(ecs && (State(Bplcon3) & BorderNotTransparent) != 0);
        }

        var bplcon2 = State(CustomRegister.Bplcon2);
        if ((bplcon2 & KeyBitplaneEnable) != 0)
            return ((planes >> ((bplcon2 >> 12) & 7)) & 1) != 0;
        if ((bplcon2 & KeyColorTableEnable) != 0)
            return (State(CustomRegister.Color00 + (register & 31) * 2) & 0x8000) != 0;
        return register == 0;
    }

    /// <summary>The color register of a pixel from the bits of its planes. With extra half-brite, it can be 32 to 63.</summary>
    private int ColorRegister(int index, bool dualPlayfield)
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

        return index;
    }

    private static uint Rgb(ushort rgb, bool halfBrite)
    {
        int red = (rgb >> 8) & 0xF, green = (rgb >> 4) & 0xF, blue = rgb & 0xF;
        if (halfBrite)
        {
            red >>= 1;
            green >>= 1;
            blue >>= 1;
        }

        return 0xFF00_0000u | (uint)(red * 17) << 16 | (uint)(green * 17) << 8 | (uint)(blue * 17);
    }

    /// <summary>
    /// Does the sprite DMA of a line. At the first line, each channel fetches its control words (SPRxPOS, SPRxCTL).
    /// From VSTART, it fetches the data words (SPRxDATA, SPRxDATB) of each line. At VSTOP, it fetches the control words
    /// of the next sprite of the channel. Control words of 0 end the channel for the frame.
    /// </summary>
    private void FetchSprites(int line)
    {
        const int enabled = DmaEnable | SpriteDma;
        if (line < FirstSpriteLine || (_custom.Dmacon & enabled) != enabled)
            return;

        _spritesFetched = true;
        _spriteWidth = _aga ? SpriteWidth(State(Fmode)) : 16;
        for (var number = 0; number < _sprites.Length; number++)
        {
            ref var sprite = ref _sprites[number];
            if (!sprite.Loaded || (sprite.Armed && line == sprite.VerticalStop))
            {
                LoadSpriteControl(number, ref sprite);
                sprite.Armed = false;
            }

            if (!sprite.Armed && line == sprite.VerticalStart && sprite.VerticalStop > sprite.VerticalStart)
                sprite.Armed = true;
            if (sprite.Armed)
                FetchSpriteData(number, ref sprite);
        }
    }

    /// <summary>Fetches the data of a line of a sprite: DATA and then DATB, each as wide as the sprite.</summary>
    private void FetchSpriteData(int number, ref Sprite sprite)
    {
        var pointer = SpritePointer(number);
        var bytes = (uint)_spriteWidth / 8;
        sprite.DataA = ReadSpriteData(pointer, bytes);
        sprite.DataB = ReadSpriteData(pointer + bytes, bytes);
        SetSpritePointer(number, pointer + bytes * 2);
    }

    private ulong ReadSpriteData(uint address, uint bytes)
    {
        ulong data = 0;
        for (uint i = 0; i < bytes; i += 2)
            data = data << 16 | _memory.Read16(address + i);
        return data;
    }

    /// <summary>The width of the sprites from the sprite bits of FMODE (bits 3 and 2): 16, 32, 32 or 64 pixels.</summary>
    private static int SpriteWidth(ushort fmode) => ((fmode >> 2) & 3) switch
    {
        0 => 16,
        3 => 64,
        _ => 32,
    };

    private void LoadSpriteControl(int number, ref Sprite sprite)
    {
        // SPRxPOS is the first word of the first fetch, and SPRxCTL the first word of the second fetch. A fetch is as
        // wide as the sprite: 2, 4 or 8 bytes.
        var pointer = SpritePointer(number);
        var bytes = (uint)_spriteWidth / 8;
        var position = _memory.Read16(pointer);
        var control = _memory.Read16(pointer + bytes);
        SetSpritePointer(number, pointer + bytes * 2);

        // SPRxPOS: VSTART bits 7-0 and HSTART bits 8-1. SPRxCTL: VSTOP bits 7-0, ATTACH, VSTART bit 8, VSTOP bit 8 and
        // HSTART bit 0.
        sprite.Loaded = true;
        sprite.VerticalStart = (position >> 8) | ((control & 4) << 6);
        sprite.VerticalStop = (control >> 8) | ((control & 2) << 7);
        sprite.HorizontalStart = ((position & 0xFF) << 1) | (control & 1);
        sprite.Attached = (control & 0x80) != 0;
    }

    private uint SpritePointer(int number)
    {
        var offset = Spr0pt + number * 4;
        return (uint)(State(offset) << 16 | State(offset + 2)) & 0x1F_FFFE;
    }

    private void SetSpritePointer(int number, uint value)
    {
        var offset = Spr0pt + number * 4;
        _state[offset >> 1] = (ushort)(value >> 16);
        _state[(offset + 2) >> 1] = (ushort)value;
    }

    /// <summary>
    /// The color register (16 to 31) of the sprite pixel at the low-resolution position, or 0 if no sprite shows
    /// there. A sprite with a lower number is in front. A sprite starts one pixel after its HSTART. Each pair of
    /// sprites has colors 17 to 19, 21 to 23, 25 to 27 or 29 to 31. An odd sprite with ATTACH makes its pair one
    /// sprite with 15 colors (17 to 31). BPLCON2 gives the pairs that are in front of the playfields. A playfield pixel
    /// of color 0 never hides a sprite.
    /// </summary>
    private int SpritePixel(int lowResolution, int playfieldIndex, bool dualPlayfield)
    {
        for (var pair = 0; pair < 4; pair++)
        {
            ref var even = ref _sprites[pair * 2];
            ref var odd = ref _sprites[pair * 2 + 1];
            var evenBits = SpriteBits(ref even, lowResolution, _spriteWidth);
            var oddBits = SpriteBits(ref odd, lowResolution, _spriteWidth);
            int color;
            if (odd.Attached)
                color = (oddBits << 2 | evenBits) is var attached and not 0 ? 16 + attached : 0;
            else if (evenBits != 0)
                color = 16 + pair * 4 + evenBits;
            else if (oddBits != 0)
                color = 16 + pair * 4 + oddBits;
            else
                color = 0;
            if (color == 0)
                continue;

            return playfieldIndex == 0 || pair < PlayfieldPriority(playfieldIndex, dualPlayfield) ? color : 0;
        }

        return 0;
    }

    /// <summary>The two bits of a sprite at the position: bit 0 from DATA and bit 1 from DATB.</summary>
    private static int SpriteBits(ref Sprite sprite, int lowResolution, int width)
    {
        if (!sprite.Armed)
            return 0;
        var bit = lowResolution - (sprite.HorizontalStart + 1);
        // A sprite of 64 pixels shows again 256 pixels to the right. The AGA port of Sonic the Hedgehog (Aonic) needs
        // this: it puts its four pairs of background sprites over 256 pixels, and its display window is 288 pixels
        // wide. The rule comes from the behavior of that program, not from a document of the hardware.
        if ((bit < 0 || bit >= width) && width == 64)
            bit = lowResolution - (sprite.HorizontalStart + 1 + 256);
        if (bit < 0 || bit >= width)
            return 0;
        var mask = 1UL << (width - 1 - bit);
        return ((sprite.DataA & mask) != 0 ? 1 : 0) | ((sprite.DataB & mask) != 0 ? 2 : 0);
    }

    /// <summary>
    /// The number of sprite pairs in front of the playfield of the pixel: PF2P (BPLCON2 bits 5-3) for a single
    /// playfield and for playfield 2, and PF1P (bits 2-0) for playfield 1.
    /// </summary>
    private int PlayfieldPriority(int index, bool dualPlayfield)
    {
        var bplcon2 = State(CustomRegister.Bplcon2);
        var playfield1 = dualPlayfield && ((index & 1) | ((index >> 1) & 2) | ((index >> 2) & 4)) != 0;
        return playfield1 ? bplcon2 & 7 : (bplcon2 >> 3) & 7;
    }

    /// <summary>Applies the copper writes of this line up to the horizontal position.</summary>
    private void ApplyWrites(ref int next, int horizontal)
    {
        while (next < _writes.Count && _writes[next].Horizontal <= horizontal)
        {
            var write = _writes[next++];
            if (_moduloPending && write.Horizontal >= _moduloPosition)
                AddModulo();
            SetRegister(write.Offset, write.Value);
        }

        if (_moduloPending && horizontal >= _moduloPosition)
            AddModulo();
    }

    private ushort State(int offset) => _state[offset >> 1];

    /// <summary>Sets a register of the display. With AGA, a color write also changes the palette.</summary>
    private void SetRegister(int offset, ushort value)
    {
        _state[offset >> 1] = value;
        if (_aga && CustomChips.IsColor(offset))
            CustomChips.WriteColor(_palette, offset, value, State(Bplcon3), aga: true);
        // A write of SPRxPOS moves the sprite at once, also while its DMA runs: a program can show a sprite again
        // further right on the same line. It also sets the start line. If that is the current line, the sprite DMA
        // starts on this line: at once if the sprite DMA of the line ran already, else with it.
        if (offset is >= Spr0pos and < Spr0pos + 64 && (offset & 7) == 0)
        {
            var number = (offset - Spr0pos) >> 3;
            ref var sprite = ref _sprites[number];
            sprite.HorizontalStart = ((value & 0xFF) << 1) | (sprite.HorizontalStart & 1);
            sprite.VerticalStart = (value >> 8) | (sprite.VerticalStart & 0x100);
            const int enabled = DmaEnable | SpriteDma;
            if (sprite.Loaded && !sprite.Armed && sprite.VerticalStart == _line && sprite.VerticalStop > _line
                && (_custom.Dmacon & enabled) == enabled)
            {
                sprite.Armed = true;
                if (_spritesFetched)
                {
                    FetchSpriteData(number, ref sprite);
                    _spritesOnLine = true;
                }
            }
        }
    }

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
