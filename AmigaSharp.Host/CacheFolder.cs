namespace AmigaSharp.Host;

/// <summary>
/// The folder where the launcher keeps the translations and the maps of the code that ran. The folder stays from run to
/// run: the system does not clean it as it cleans the temporary folder.
/// </summary>
public static class CacheFolder
{
    /// <summary>The environment variable that sets another folder.</summary>
    public const string Variable = "AMIGASHARP_CACHE";

    /// <summary>The folder for this user and this operating system.</summary>
    public static string Default { get; } = Find(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The folder of the translations before 2026-10-01, in the temporary folder.</summary>
    public static string OldTranslations { get; } = Path.Combine(Path.GetTempPath(), "AmigaSharp", "translations");

    /// <summary>
    /// Finds the folder: <see cref="Variable"/> if it is set, else %LOCALAPPDATA%\AmigaSharp on Windows,
    /// ~/Library/Caches/AmigaSharp on macOS, and $XDG_CACHE_HOME/amigasharp (or ~/.cache/amigasharp) on Linux.
    /// </summary>
    public static string Find(string system, Func<string, string?> environment, string home)
    {
        if (environment(Variable) is { Length: > 0 } folder)
            return folder;
        return system switch
        {
            "windows" => Path.Combine(environment("LOCALAPPDATA") is { Length: > 0 } local
                ? local
                : Path.Combine(home, "AppData", "Local"), "AmigaSharp"),
            "macos" => Path.Combine(home, "Library", "Caches", "AmigaSharp"),
            _ => Path.Combine(environment("XDG_CACHE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(home, ".cache"), "amigasharp"),
        };
    }
}
