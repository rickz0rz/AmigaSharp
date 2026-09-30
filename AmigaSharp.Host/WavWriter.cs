using System.Buffers.Binary;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Host;

/// <summary>
/// Writes the sound of the audio channels to a WAV file: 16-bit stereo PCM at <see cref="AudioOutput.SampleRate"/>. A
/// thread takes the samples each 50 ms. The file gets its sizes when it closes.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private const int HeaderSize = 44;
    private readonly FileStream _file;
    private readonly AudioOutput _audio;
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _dataBytes;

    public WavWriter(string path, AudioOutput audio)
    {
        _audio = audio;
        _file = File.Create(path);
        _file.Write(new byte[HeaderSize]);
        _thread = new Thread(Run) { IsBackground = true, Name = "WAV writer" };
        _thread.Start();
    }

    private void Run()
    {
        var samples = new short[AudioOutput.SampleRate * 2];
        var bytes = new byte[samples.Length * 2];
        while (!_stop)
        {
            Thread.Sleep(50);
            Drain(samples, bytes);
        }

        Drain(samples, bytes);
    }

    private void Drain(short[] samples, byte[] bytes)
    {
        var count = _audio.Read(samples) * 2;
        for (var i = 0; i < count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        _file.Write(bytes, 0, count * 2);
        _dataBytes += count * 2;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
        var header = new byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), (int)(_dataBytes + HeaderSize - 8));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 2); // Stereo
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), AudioOutput.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), AudioOutput.SampleRate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 4);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), (int)_dataBytes);
        _file.Position = 0;
        _file.Write(header);
        _file.Dispose();
    }
}
