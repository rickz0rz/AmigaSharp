using AmigaSharp.Runtime.Hardware;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace AmigaSharp.Host;

/// <summary>
/// Shows the picture of the display in an SDL window. The window must run on the main thread, because macOS requires
/// that for windows.
/// </summary>
/// <remarks>
/// <para>
/// The host mouse is the mouse in port 1: a move of one low-resolution pixel of the picture is one count, whatever the
/// size of the window. A game controller of the host is the joystick in port 2: the D-pad or the left stick, and A, B
/// and X for fire buttons 1, 2 and 3.
/// </para>
/// <para>
/// The sound of the audio channels plays on the default audio device of the host, about 100 ms late. When the program
/// makes the sound faster than real time, the window drops the samples that the device cannot play in time.
/// </para>
/// </remarks>
public sealed unsafe class DisplayWindow(
    Display display, string title, int scale, Action<Scancode, bool> key, ControllerPort mouse, ControllerPort joystick,
    AudioOutput? audio = null)
{
    private const int StickThreshold = 16_000;
    private const int BytesPerSample = 4;
    private const uint AudioS16Lsb = 0x8010;
    private static readonly uint TargetQueueBytes = AudioOutput.SampleRate / 10 * BytesPerSample;
    private readonly short[] _samples = new short[AudioOutput.SampleRate];
    private double _mouseX;
    private double _mouseY;

    /// <summary>Shows the window until the user closes it or <paramref name="stop"/> becomes true.</summary>
    public void Run(Func<bool> stop)
    {
        var sdl = Sdl.GetApi();
        // Without this hint, Windows enlarges the window by the scaling of the display, and blurs it. At 150 %, a
        // window at scale 2 is then larger than a 1920 by 1080 screen. With the hint, the size of the window is in
        // pixels on Windows. On macOS, the size stays in points.
        sdl.SetHint("SDL_WINDOWS_DPI_AWARENESS", "permonitorv2");
        if (sdl.Init(Sdl.InitVideo | Sdl.InitGamecontroller | Sdl.InitAudio) != 0)
            throw new InvalidOperationException($"SDL cannot start: {sdl.GetErrorS()}");
        var audioDevice = audio == null ? 0 : OpenAudio(sdl);

        var window = sdl.CreateWindow(title, Sdl.WindowposCentered, Sdl.WindowposCentered,
            Display.Width * scale, Display.Height * scale,
            (uint)(WindowFlags.Hidden | WindowFlags.AllowHighdpi | WindowFlags.Resizable));
        FitToDisplay(sdl, window);
        sdl.ShowWindow(window);
        var renderer = sdl.CreateRenderer(window, -1, (uint)(RendererFlags.Accelerated | RendererFlags.Presentvsync));
        // The picture keeps its shape in a window of any size, with black bars at the sides or at the top and bottom.
        sdl.RenderSetLogicalSize(renderer, Display.Width, Display.Height);
        var texture = sdl.CreateTexture(renderer, (uint)PixelFormatEnum.Argb8888, (int)TextureAccess.Streaming,
            Display.Width, Display.Height);
        var pixels = new uint[Display.Width * Display.Height];
        var shown = -1L;

        try
        {
            while (!stop())
            {
                Event e;
                while (sdl.PollEvent(&e) != 0)
                {
                    if (e.Type == (uint)EventType.Quit)
                        return;
                    // A key that the host repeats sends a new key down. The Amiga repeats a key in its own way.
                    if (e.Type is (uint)EventType.Keydown or (uint)EventType.Keyup && e.Key.Repeat == 0)
                        key(e.Key.Keysym.Scancode, e.Type == (uint)EventType.Keyup);
                    else
                        Controllers(sdl, e);
                }

                if (audioDevice != 0)
                    QueueAudio(sdl, audioDevice);

                if (display.FrameNumber != shown)
                {
                    shown = display.FrameNumber;
                    display.CopyFrame(pixels);
                    fixed (uint* data = pixels)
                        sdl.UpdateTexture(texture, null, data, Display.Width * 4);
                }

                sdl.RenderClear(renderer);
                sdl.RenderCopy(renderer, texture, null, null);
                // With vsync, this waits for the next frame of the host display.
                sdl.RenderPresent(renderer);
            }
        }
        finally
        {
            if (audioDevice != 0)
                sdl.CloseAudioDevice(audioDevice);
            sdl.DestroyTexture(texture);
            sdl.DestroyRenderer(renderer);
            sdl.DestroyWindow(window);
            sdl.Quit();
        }
    }

    /// <summary>
    /// Makes the window smaller, with the same shape, if the window and its borders do not fit in the usable area of
    /// its display (the area without the taskbar or the menu bar). Then centers it in that area.
    /// </summary>
    private static void FitToDisplay(Sdl sdl, Window* window)
    {
        Rectangle<int> usable;
        if (sdl.GetDisplayUsableBounds(sdl.GetWindowDisplayIndex(window), &usable) != 0)
            return;
        int top = 0, left = 0, bottom = 0, right = 0;
        sdl.GetWindowBordersSize(window, &top, &left, &bottom, &right);
        var availableWidth = usable.Size.X - left - right;
        var availableHeight = usable.Size.Y - top - bottom;

        int width, height;
        sdl.GetWindowSize(window, &width, &height);
        if (width <= availableWidth && height <= availableHeight)
            return;

        var factor = Math.Min((double)availableWidth / Display.Width, (double)availableHeight / Display.Height);
        width = Math.Max(1, (int)(Display.Width * factor));
        height = Math.Max(1, (int)(Display.Height * factor));
        sdl.SetWindowSize(window, width, height);
        sdl.SetWindowPosition(window, usable.Origin.X + left + (availableWidth - width) / 2,
            usable.Origin.Y + top + (availableHeight - height) / 2);
    }

    private static uint OpenAudio(Sdl sdl)
    {
        var desired = new AudioSpec { Freq = AudioOutput.SampleRate, Format = (ushort)AudioS16Lsb, Channels = 2, Samples = 1024 };
        AudioSpec obtained;
        var device = sdl.OpenAudioDevice((byte*)null, 0, &desired, &obtained, 0);
        if (device == 0)
        {
            Console.Error.WriteLine($"The sound is off: SDL cannot open the audio device: {sdl.GetErrorS()}");
            return 0;
        }

        sdl.PauseAudioDevice(device, 0);
        return device;
    }

    /// <summary>Keeps about 100 ms of sound in the queue of the device, and drops the samples that would be late.</summary>
    private void QueueAudio(Sdl sdl, uint device)
    {
        var queued = sdl.GetQueuedAudioSize(device);
        var wanted = queued < TargetQueueBytes ? (int)((TargetQueueBytes - queued) / BytesPerSample) : 0;
        var late = audio!.Available - wanted;
        while (late > 0)
            late -= audio.Read(_samples.AsSpan(0, Math.Min(late, _samples.Length / 2) * 2));
        if (wanted == 0)
            return;
        var count = audio.Read(_samples.AsSpan(0, Math.Min(wanted, _samples.Length / 2) * 2));
        fixed (short* data = _samples)
            sdl.QueueAudio(device, data, (uint)(count * BytesPerSample));
    }

    private void Controllers(Sdl sdl, Event e)
    {
        switch ((EventType)e.Type)
        {
            case EventType.Mousemotion:
            {
                // The renderer has the logical size of the picture, so SDL gives the move in high-resolution pixels of
                // the picture, whatever the size of the window. Two of them are one count. Keep the fractions, so that
                // a slow move also moves the Amiga mouse.
                _mouseX += e.Motion.Xrel / 2.0;
                _mouseY += e.Motion.Yrel / 2.0;
                var dx = (int)_mouseX;
                var dy = (int)_mouseY;
                _mouseX -= dx;
                _mouseY -= dy;
                mouse.MoveMouse(dx, dy);
                break;
            }
            case EventType.Mousebuttondown or EventType.Mousebuttonup:
                if (MouseButton(e.Button.Button) is { } button)
                    mouse.SetButton(button, e.Type == (uint)EventType.Mousebuttondown);
                break;
            case EventType.Controllerdeviceadded:
                sdl.GameControllerOpen(e.Cdevice.Which);
                break;
            case EventType.Controllerbuttondown or EventType.Controllerbuttonup:
            {
                var pressed = e.Type == (uint)EventType.Controllerbuttondown;
                switch ((GameControllerButton)e.Cbutton.Button)
                {
                    case GameControllerButton.A: joystick.SetButton(ControllerButton.Left, pressed); break;
                    case GameControllerButton.B: joystick.SetButton(ControllerButton.Right, pressed); break;
                    case GameControllerButton.X: joystick.SetButton(ControllerButton.Middle, pressed); break;
                    case GameControllerButton.DpadUp: joystick.SetDirection(JoystickDirection.Up, pressed); break;
                    case GameControllerButton.DpadDown: joystick.SetDirection(JoystickDirection.Down, pressed); break;
                    case GameControllerButton.DpadLeft: joystick.SetDirection(JoystickDirection.Left, pressed); break;
                    case GameControllerButton.DpadRight: joystick.SetDirection(JoystickDirection.Right, pressed); break;
                }

                break;
            }
            case EventType.Controlleraxismotion:
                switch ((GameControllerAxis)e.Caxis.Axis)
                {
                    case GameControllerAxis.Leftx:
                        joystick.SetDirection(JoystickDirection.Left, e.Caxis.Value < -StickThreshold);
                        joystick.SetDirection(JoystickDirection.Right, e.Caxis.Value > StickThreshold);
                        break;
                    case GameControllerAxis.Lefty:
                        joystick.SetDirection(JoystickDirection.Up, e.Caxis.Value < -StickThreshold);
                        joystick.SetDirection(JoystickDirection.Down, e.Caxis.Value > StickThreshold);
                        break;
                }

                break;
        }
    }

    private static ControllerButton? MouseButton(byte button) => button switch
    {
        1 => ControllerButton.Left,
        2 => ControllerButton.Middle,
        3 => ControllerButton.Right,
        _ => null,
    };
}
