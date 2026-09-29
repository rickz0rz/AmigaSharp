using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AmigaSharp.Launcher;

/// <summary>
/// Plays the audio files of a playlist in a loop: the music of the stream. The feed decodes each file with ffmpeg to
/// PCM (48 kHz, stereo, 16 bits). The stream reads the samples with <see cref="Read"/>, as it needs them. If the
/// playlist has no audio that plays, the feed gives silence for 10 seconds, and then reads the playlist again.
/// </summary>
/// <remarks>
/// The playlist is an M3U file or a text file with one audio file on each line, or a directory of audio files. Lines
/// that start with '#' are comments. A path that is not absolute is relative to the directory of the playlist. The
/// feed reads the playlist again at the start of each loop, so a change of the playlist plays in the next loop.
/// </remarks>
public sealed class AudioFeed : IDisposable
{
    public const int SampleRate = 48000;

    private static readonly TimeSpan SilenceBeforeRetry = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".aif", ".aiff", ".wma",
    };

    private readonly string _playlist;
    private readonly TextWriter _log;
    private List<string> _files = [];
    private int _next;
    private bool _playedInLoop;
    private Process? _decoder;
    private string? _file;
    private bool _fileHasAudio;
    private long _silenceLeft;
    private byte[] _bytes = [];

    public AudioFeed(string playlist, TextWriter log)
    {
        _playlist = playlist;
        _log = log;
    }

    public void Dispose() => StopDecoder();

    public static List<string> ReadPlaylist(string playlist)
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

    /// <summary>Fills the target with the next stereo samples (left, right, ...).</summary>
    public void Read(Span<short> target)
    {
        var filled = 0;
        while (filled < target.Length)
        {
            if (_silenceLeft > 0)
            {
                var count = (int)Math.Min(_silenceLeft, target.Length - filled);
                target.Slice(filled, count).Clear();
                _silenceLeft -= count;
                filled += count;
                continue;
            }

            if (_decoder == null && !StartNextFile())
            {
                _silenceLeft = (long)(SilenceBeforeRetry.TotalSeconds * SampleRate * 2);
                continue;
            }

            // Read whole stereo samples: 4 bytes each.
            var wanted = (target.Length - filled) * 2;
            if (_bytes.Length < wanted)
                _bytes = new byte[wanted];
            var read = _decoder!.StandardOutput.BaseStream.ReadAtLeast(_bytes.AsSpan(0, wanted), wanted,
                throwOnEndOfStream: false) & ~3;
            if (read > 0)
            {
                _fileHasAudio = true;
                MemoryMarshal.Cast<byte, short>(_bytes.AsSpan(0, read)).CopyTo(target[filled..]);
                filled += read / 2;
            }

            if (read < wanted)
                EndFile();
        }
    }

    /// <summary>Starts the decoder of the next file of the playlist. Returns false if a whole loop played nothing.</summary>
    private bool StartNextFile()
    {
        if (_next >= _files.Count)
        {
            // A new loop: the last loop must have played something, or the playlist has no audio that plays.
            var nothingPlayed = _files.Count > 0 && !_playedInLoop;
            _files = ReadPlaylist(_playlist);
            _next = 0;
            _playedInLoop = false;
            if (_files.Count == 0 || nothingPlayed)
            {
                if (_files.Count > 0)
                    _log.WriteLine($"The audio playlist {_playlist} has no audio that plays. The music is silent.");
                _files = [];
                return false;
            }
        }

        _file = _files[_next++];
        _fileHasAudio = false;
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin", "-i", _file, "-vn",
                     "-f", "s16le", "-ar", SampleRate.ToString(), "-ac", "2", "pipe:1",
                 })
            start.ArgumentList.Add(argument);
        try
        {
            _decoder = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _decoder = null;
        }

        if (_decoder == null)
            return false;
        _decoder.ErrorDataReceived += (_, _) => { };
        _decoder.BeginErrorReadLine();
        return true;
    }

    private void EndFile()
    {
        if (_fileHasAudio)
            _playedInLoop = true;
        else
            _log.WriteLine($"The audio file {_file} did not play.");
        StopDecoder();
    }

    private void StopDecoder()
    {
        if (_decoder == null)
            return;
        try
        {
            if (!_decoder.HasExited)
                _decoder.Kill();
            _decoder.WaitForExit();
        }
        catch (InvalidOperationException)
        {
        }

        _decoder.Dispose();
        _decoder = null;
    }
}
