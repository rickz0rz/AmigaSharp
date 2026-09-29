using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Dos;
using AmigaSharp.Runtime.Hardware;
using AmigaSharp.Runtime.Input;

namespace AmigaSharp.Tests.Translator;

/// <summary>
/// Runs the translated target program on a copy of the drive of the Prevue machine, with a virtual clock, and checks
/// the picture. Without the listing data from the serial port, Prevue shows its grid with the time bar, the logo and
/// the message "Please Stand By".
/// </summary>
public sealed class TargetRunTests : IDisposable
{
    // The colors of the picture.
    // The area of the genlock video: color 0, black, and the genlock key (alpha 0).
    private const uint Genlock = 0x0000_0000;
    private const uint Background = 0xFF00_0033;
    private const uint GridCell = 0xFF22_3388;
    private const uint Text = 0xFFCC_CC00;
    private const uint Banner = 0xFF55_1122;
    private const uint MenuPanel = 0xFF55_5555;
    private const uint ListingCell = 0xFF00_0055;

    private readonly string _drive = Directory.CreateTempSubdirectory("AmigaSharp-drive-").FullName;
    private readonly string _ram = Directory.CreateTempSubdirectory("AmigaSharp-RAM-").FullName;

    [Fact]
    public void WithoutListingData_ShowsTheGrid()
    {
        // The grid shows after the startup, at about 10 seconds. It scrolls, so each part of the picture must be in at
        // least one of the frames.
        var frames = Run(seconds: 16, firstFrame: 11);

        Assert.All(frames, pixels => Assert.True(Count(pixels, Background) > 50_000, "The background of the grid is missing."));
        Assert.Contains(frames, pixels => Count(pixels, Genlock) > 100_000);
        Assert.Contains(frames, pixels => Count(pixels, GridCell) > 5_000);
        Assert.Contains(frames, pixels => Count(pixels, Text) > 1_000);
        Assert.Contains(frames, pixels => Count(pixels, Banner) > 2_000);
    }

    [Fact]
    public void WithTheSavedListings_ShowsThePrograms()
    {
        // The listing files of the drive are from 1 November 2020, and they are packed with PowerPacker. Prevue reads
        // them unpacked, and it shows them when the date of the Amiga is their date.
        var frames = Run(seconds: 16, firstFrame: 12, date: new DateTime(2020, 11, 1, 16, 0, 0), unpack: true);

        Assert.Contains(frames, pixels => Count(pixels, ListingCell) > 40_000 && Count(pixels, Text) > 8_000);
    }

    [Fact]
    public void Escape_OpensTheMenu()
    {
        var pixels = Run(seconds: 14, firstFrame: 14, pressEscapeAt: 11)[^1];

        Assert.True(Count(pixels, MenuPanel) > 50_000, "The menu is missing.");
        Assert.True(Count(pixels, Text) > 1_000, "The text of the menu is missing.");
    }

    /// <summary>Runs the program and returns a frame each half second from <paramref name="firstFrame"/> seconds.</summary>
    private List<uint[]> Run(double seconds, double firstFrame, double? pressEscapeAt = null, DateTime? date = null,
        bool unpack = false)
    {
        if (!TargetProgram.IsBuilt || !Directory.Exists(TargetProgram.DriveDirectory))
            Assert.Skip("The target or the drive is missing. Run scripts/build-target.sh and copy the drive to target-source/binaries.");

        CopyDirectory(TargetProgram.DriveDirectory, _drive);
        if (unpack)
        {
            foreach (var file in Directory.GetFiles(_drive))
            {
                var data = File.ReadAllBytes(file);
                if (PowerPacker.IsPacked(data))
                    File.WriteAllBytes(file, PowerPacker.Unpack(data));
            }
        }

        var clock = new VirtualClock();
        var core = new Core(new MemoryStream(), new MemoryStream(), _drive, clock) { Log = TextWriter.Null };
        if (date != null)
            core.SetDate(date.Value);
        core.FileSystem.AddVolume("DH1", _drive);
        core.FileSystem.AddAssign("DF0", "DH1:");
        core.FileSystem.AddAssign("ENV", "DH1:");
        Directory.CreateDirectory(Path.Combine(_ram, "T"));
        core.FileSystem.AddVolume("RAM", _ram);
        core.FileSystem.AddAssign("T", "RAM:T");

        var program = (TranslatedProgram)Activator.CreateInstance(TargetProgram.Type, core)!;
        var executable = File.ReadAllBytes(TargetProgram.ExecutablePath);
        var runner = new Thread(() =>
        {
            try
            {
                program.Run(executable, "GA24005", "esq");
            }
            catch (Exception)
            {
                // The test checks the picture.
            }
        }, 64 * 1024 * 1024) { IsBackground = true };
        runner.Start();

        var pressed = false;
        var frames = new List<uint[]>();
        var nextFrame = firstFrame;
        var deadline = DateTime.Now.AddMinutes(2);
        while (clock.Elapsed.TotalSeconds < seconds && runner.IsAlive && DateTime.Now < deadline)
        {
            if (clock.Elapsed.TotalSeconds >= nextFrame)
            {
                frames.Add(Frame(core));
                nextFrame += 0.5;
            }

            if (pressEscapeAt is { } time && !pressed && clock.Elapsed.TotalSeconds >= time)
            {
                core.KeyboardInput.PostRawKey(RawKey.Escape, up: false);
                core.KeyboardInput.PostRawKey(RawKey.Escape, up: true);
                pressed = true;
            }

            Thread.Sleep(10);
        }

        Assert.True(clock.Elapsed.TotalSeconds >= seconds, $"The program stopped at {clock.Elapsed.TotalSeconds:F1} seconds.");
        frames.Add(Frame(core));
        return frames;
    }

    private static uint[] Frame(Core core)
    {
        var pixels = new uint[Display.Width * Display.Height];
        core.Chipset.Display.CopyFrame(pixels);
        return pixels;
    }

    /// <summary>
    /// Counts the pixels of a color. An opaque color matches its RGB value, with or without the genlock key: for
    /// example, the border has the color of the grid background and is the key. A color with alpha 0 must match
    /// exactly.
    /// </summary>
    private static int Count(uint[] pixels, uint color) =>
        color >> 24 == 0 ? pixels.Count(pixel => pixel == color) : pixels.Count(pixel => (pixel | 0xFF00_0000) == color);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    public void Dispose()
    {
        // The program can still run on its thread, so a file can be in use.
        try
        {
            Directory.Delete(_drive, recursive: true);
            Directory.Delete(_ram, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
