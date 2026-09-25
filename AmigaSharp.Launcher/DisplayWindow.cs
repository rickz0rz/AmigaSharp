using AmigaSharp.Runtime.Hardware;
using Silk.NET.SDL;

namespace AmigaSharp.Launcher;

/// <summary>
/// Shows the picture of the display in an SDL window. The window must run on the main thread, because macOS requires
/// that for windows.
/// </summary>
public sealed unsafe class DisplayWindow(Display display, string title, int scale)
{
    /// <summary>Shows the window until the user closes it or <paramref name="stop"/> becomes true.</summary>
    public void Run(Func<bool> stop)
    {
        var sdl = Sdl.GetApi();
        if (sdl.Init(Sdl.InitVideo) != 0)
            throw new InvalidOperationException($"SDL cannot start: {sdl.GetErrorS()}");

        var window = sdl.CreateWindow(title, Sdl.WindowposCentered, Sdl.WindowposCentered,
            Display.Width * scale, Display.Height * scale, (uint)(WindowFlags.Shown | WindowFlags.AllowHighdpi));
        var renderer = sdl.CreateRenderer(window, -1, (uint)(RendererFlags.Accelerated | RendererFlags.Presentvsync));
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
}
