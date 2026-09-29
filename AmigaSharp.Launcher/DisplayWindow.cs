using AmigaSharp.Runtime.Hardware;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace AmigaSharp.Launcher;

/// <summary>
/// Shows the picture of the display in an SDL window. The window must run on the main thread, because macOS requires
/// that for windows.
/// </summary>
public sealed unsafe class DisplayWindow(Display display, string title, int scale, Action<Scancode, bool> key)
{
    /// <summary>Shows the window until the user closes it or <paramref name="stop"/> becomes true.</summary>
    public void Run(Func<bool> stop)
    {
        var sdl = Sdl.GetApi();
        // Without this hint, Windows enlarges the window by the scaling of the display, and blurs it. At 150 %, a
        // window at scale 2 is then larger than a 1920 by 1080 screen. With the hint, the size of the window is in
        // pixels on Windows. On macOS, the size stays in points.
        sdl.SetHint("SDL_WINDOWS_DPI_AWARENESS", "permonitorv2");
        if (sdl.Init(Sdl.InitVideo) != 0)
            throw new InvalidOperationException($"SDL cannot start: {sdl.GetErrorS()}");

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
                }

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
}
