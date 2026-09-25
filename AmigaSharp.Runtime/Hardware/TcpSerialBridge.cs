using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AmigaSharp.Runtime.Hardware;

/// <summary>
/// Connects the serial port to TCP, as emulators such as FS-UAE and WinUAE do. The bridge listens on a port. The
/// bytes from the client go to the serial port, and the bytes from the serial port go to the client. When a new
/// client connects, it replaces the old client. For example, <c>nc localhost 5400</c> connects a terminal.
/// </summary>
public sealed class TcpSerialBridge : ISerialConnection, IDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentQueue<byte> _received = new();
    private readonly TextWriter _log;
    private readonly object _lock = new();
    private TcpClient? _client;
    private volatile bool _disposed;

    /// <param name="port">The TCP port. 0 selects a free port: see <see cref="Port"/>.</param>
    /// <param name="address">The address to listen on. The default is the loopback address.</param>
    public TcpSerialBridge(int port, IPAddress? address = null, TextWriter? log = null)
    {
        _log = log ?? TextWriter.Null;
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptClients) { IsBackground = true, Name = "Serial bridge" }.Start();
    }

    public int Port { get; }

    public bool IsConnected
    {
        get
        {
            lock (_lock)
                return _client is { Connected: true };
        }
    }

    public int Available => _received.Count;

    public bool TryRead(out byte value) => _received.TryDequeue(out value);

    public void Write(byte value)
    {
        lock (_lock)
        {
            if (_client == null)
                return;
            try
            {
                _client.GetStream().WriteByte(value);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Disconnect(_client);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _listener.Stop();
        lock (_lock)
            _client?.Dispose();
    }

    private void AcceptClients()
    {
        while (!_disposed)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            client.NoDelay = true;
            lock (_lock)
            {
                _client?.Dispose();
                _client = client;
            }

            _log.WriteLine($"Serial bridge: {client.Client.RemoteEndPoint} connected.");
            new Thread(() => ReceiveFrom(client)) { IsBackground = true, Name = "Serial bridge receive" }.Start();
        }
    }

    private void ReceiveFrom(TcpClient client)
    {
        var buffer = new byte[4096];
        try
        {
            var stream = client.GetStream();
            while (true)
            {
                var count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0)
                    break;
                for (var i = 0; i < count; i++)
                    _received.Enqueue(buffer[i]);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        lock (_lock)
            Disconnect(client);
    }

    private void Disconnect(TcpClient client)
    {
        if (_client != client)
            return;
        _log.WriteLine("Serial bridge: the client disconnected.");
        _client.Dispose();
        _client = null;
    }
}
