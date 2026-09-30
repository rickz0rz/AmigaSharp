namespace AmigaSharp.Host;

/// <summary>
/// Reads a playlist of audio files: an M3U file, a text file with one audio file on each line, or a directory of audio
/// files. Lines that start with '#' are comments. A path that is not absolute is relative to the directory of the
/// playlist.
/// </summary>
public static class PlaylistFile
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".aif", ".aiff", ".wma",
    };

    /// <summary>The audio files of the playlist, in its order. Files that do not exist are not in the list.</summary>
    public static List<string> Read(string playlist)
    {
        if (Directory.Exists(playlist))
        {
            return Directory.EnumerateFiles(playlist)
                .Where(file => AudioExtensions.Contains(Path.GetExtension(file)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!File.Exists(playlist))
            return [];
        var directory = Path.GetDirectoryName(Path.GetFullPath(playlist))!;
        return File.ReadLines(playlist)
            .Select(line => line.Trim().TrimStart('﻿'))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(directory, line)))
            .Where(File.Exists)
            .ToList();
    }
}

