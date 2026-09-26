using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AmigaSharp.Launcher;

/// <summary>
/// Plays the audio files of a playlist in a loop, as the sound of the video stream. The feed decodes each file with
/// ffmpeg to PCM (48 kHz, stereo, 16 bits) and sends the samples at the rate of real time over a local TCP connection
/// to the encoder of the stream. If the playlist has no audio that plays, the feed sends silence, so the sound of the
/// stream never stops.
/// </summary>
/// <remarks>
/// The playlist is an M3U file or a text file with one audio file on each line, or a directory of audio files. Lines
/// that start with '#' are comments. A path that is not absolute is relative to the directory of the playlist. The
/// feed reads the playlist again at the start of each loop, so a change of the playlist plays in the next loop.
/// </remarks>
public sealed class AudioFeed : IDisposable
{
    public const int SampleRate = 48000;
    private const int BytesPerSecond = SampleRate * 2 * 2;

    // The feed sends the samples in blocks of 20 ms, and sends silence for 10 seconds when the playlist has no audio.
    private const int BlockBytes = BytesPerSecond / 50;
    private static readonly TimeSpan SilenceBeforeRetry = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".aif", ".aiff", ".wma",
    };

    private readonly string _playlist;
    private readonly TextWriter _log;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private volatile bool _stopped;
    private Process? _decoder;
    private long _sentBytes;
    private readonly Stopwatch _clock = new();

    public AudioFeed(string playlist, TextWriter log)
    {
        _playlist = playlist;
        _log = log;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(Run) { IsBackground = true, Name = "Audio feed" }.Start();
    }

    /// <summary>The local TCP port. The encoder connects to it and reads the samples.</summary>
    public int Port { get; }

    public void Dispose()
    {
        _stopped = true;
        _listener.Stop();
        try
        {
            _decoder?.Kill();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>The audio files of the playlist, in its order. Files that do not exist are not in the list.</summary>
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

    private void Run()
    {
        TcpClient client;
        try
        {
            client = _listener.AcceptTcpClient();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return;
        }

        using (client)
        {
            var output = client.GetStream();
            _clock.Start();
            try
            {
                while (!_stopped)
                {
                    var files = ReadPlaylist(_playlist);
                    var played = false;
                    foreach (var file in files)
                    {
                        if (_stopped)
                            return;
                        played |= Play(file, output);
                    }

                    if (!played)
                    {
                        if (files.Count > 0)
                            _log.WriteLine($"The audio playlist {_playlist} has no audio that plays. The stream is silent.");
                        SendSilence(output, SilenceBeforeRetry);
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The encoder stopped.
            }
        }
    }

    /// <summary>Decodes a file and sends its samples. Returns false if the file has no audio that ffmpeg can decode.</summary>
    private bool Play(string file, Stream output)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin", "-i", file, "-vn",
                     "-f", "s16le", "-ar", SampleRate.ToString(), "-ac", "2", "pipe:1",
                 })
            start.ArgumentList.Add(argument);

        using var decoder = Process.Start(start);
        if (decoder == null)
            return false;
        _decoder = decoder;
        decoder.ErrorDataReceived += (_, _) => { };
        decoder.BeginErrorReadLine();

        var input = decoder.StandardOutput.BaseStream;
        var block = new byte[BlockBytes];
        var any = false;
        while (!_stopped)
        {
            var count = ReadBlock(input, block);
            if (count == 0)
                break;
            any = true;
            Send(output, block.AsSpan(0, count));
        }

        if (!decoder.HasExited)
            decoder.Kill();
        decoder.WaitForExit();
        _decoder = null;
        if (!any)
            _log.WriteLine($"The audio file {file} did not play.");
        return any;
    }

    private void SendSilence(Stream output, TimeSpan time)
    {
        var block = new byte[BlockBytes];
        for (var sent = TimeSpan.Zero; sent < time && !_stopped; sent += TimeSpan.FromMilliseconds(20))
            Send(output, block);
    }

    /// <summary>Sends samples at the rate of real time: the feed waits until the clock is at the time of the samples.</summary>
    private void Send(Stream output, ReadOnlySpan<byte> samples)
    {
        var due = TimeSpan.FromSeconds((double)_sentBytes / BytesPerSecond);
        // Keep 200 ms ahead of the clock, so that the encoder always has samples.
        var wait = due - _clock.Elapsed - TimeSpan.FromMilliseconds(200);
        if (wait > TimeSpan.Zero)
            Thread.Sleep(wait);
        output.Write(samples);
        _sentBytes += samples.Length;
    }

    /// <summary>Fills the block, unless the stream ends. Returns the number of bytes, a multiple of 4.</summary>
    private static int ReadBlock(Stream input, byte[] block)
    {
        var count = 0;
        while (count < block.Length)
        {
            var read = input.Read(block, count, block.Length - count);
            if (read == 0)
                break;
            count += read;
        }

        return count - count % 4;
    }
}
