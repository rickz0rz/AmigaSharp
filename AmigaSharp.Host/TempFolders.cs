using System.Diagnostics;
using System.Globalization;

namespace AmigaSharp.Host;

/// <summary>
/// The temporary folders of a launcher: RAM:, the copy of a disk image, and the files of the stream. A launcher that
/// stops normally removes its folders. A launcher that crashes or that is killed cannot, so the next launcher removes
/// them when it starts.
/// </summary>
/// <remarks>
/// Each folder has an owner file next to it (the folder name and ".owner"): the process ID and the start time of the
/// launcher. The file is not in the folder, because the program can see the files of RAM:. A folder is old when its
/// launcher does not run: no process has the ID, or the process with the ID started at another time.
/// </remarks>
public static class TempFolders
{
    /// <summary>The prefixes of the names of the folders.</summary>
    public static readonly string[] Prefixes = ["AmigaSharp-RAM-", "AmigaSharp-Disk-", "AmigaSharp-stream-"];

    private const string OwnerExtension = ".owner";

    /// <summary>A folder without an owner file (from an older launcher) is old after this time without a change.</summary>
    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(1);

    /// <summary>Makes a new temporary folder with the prefix, and its owner file.</summary>
    /// <param name="root">The directory of the folder. The default is the temporary directory of the system.</param>
    public static string Create(string prefix, string? root = null)
    {
        var folder = root == null
            ? Directory.CreateTempSubdirectory(prefix).FullName
            : Directory.CreateDirectory(Path.Combine(root, prefix + Path.GetRandomFileName().Replace(".", ""))).FullName;
        using var process = Process.GetCurrentProcess();
        File.WriteAllText(folder + OwnerExtension,
            $"{process.Id}\n{process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}\n");
        return folder;
    }

    /// <summary>Removes a folder and its owner file.</summary>
    public static void Delete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }

        File.Delete(folder + OwnerExtension);
    }

    /// <summary>Removes the folders of launchers that do not run. Returns the number of folders that it removed.</summary>
    public static int RemoveOld(TextWriter log, string? root = null, Func<int, long, bool>? isRunning = null)
    {
        root ??= Path.GetTempPath();
        isRunning ??= IsRunning;
        var removed = 0;
        foreach (var prefix in Prefixes)
        {
            foreach (var folder in Directory.EnumerateDirectories(root, prefix + "*"))
            {
                if (!IsOld(folder, isRunning))
                    continue;
                try
                {
                    Delete(folder);
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.WriteLine($"The launcher cannot remove the old folder {folder}: {e.Message}");
                }
            }
        }

        if (removed > 0)
            log.WriteLine($"Removed {removed} old temporary folders of launchers that stopped.");
        return removed;
    }

    private static bool IsOld(string folder, Func<int, long, bool> isRunning)
    {
        var owner = folder + OwnerExtension;
        if (!File.Exists(owner))
            return DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > OrphanAge;
        var lines = File.ReadAllLines(owner);
        return lines.Length < 2 ||
               !int.TryParse(lines[0], CultureInfo.InvariantCulture, out var id) ||
               !long.TryParse(lines[1], CultureInfo.InvariantCulture, out var start) ||
               !isRunning(id, start);
    }

    /// <summary>True if a process with the ID runs, and started at the time (in UTC ticks).</summary>
    private static bool IsRunning(int id, long start)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return Math.Abs(process.StartTime.ToUniversalTime().Ticks - start) < TimeSpan.TicksPerSecond;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
