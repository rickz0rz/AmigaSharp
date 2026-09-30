using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.PrevueLauncher;

/// <summary>
/// Connects the control line of Prevue (CTRL, see docs/ctrl-line.md) to the CTS line of the chipset. The bytes come
/// from a TCP port or a file, and from the requests of /prevue/ctrl (see <see cref="ControlLineRequests"/>).
/// </summary>
/// <remarks>
/// Prevue samples the line in its AUD1 interrupt, 1100 times each second. When the runtime is late, the interrupts
/// come close together, so the line uses the time of the last AUD1 request, not the clock (see
/// <see cref="CustomChips.AudioSampleTime"/>). Prevue resets the state of the line while it starts, so the line keeps
/// its bytes until one second after Prevue enables the AUD1 interrupt.
/// </remarks>
public sealed class PrevueControlLine : IDisposable
{
    private readonly TcpSerialBridge? _bridge;

    /// <param name="port">A TCP port for the bytes of the line, or 0 for none.</param>
    /// <param name="file">A file of bytes to send on the line in place of the port, or null for none.</param>
    public PrevueControlLine(Core core, IClock clock, int port, string? file, TextWriter log)
    {
        var line = core.Chipset.CtsLine;
        var custom = core.Chipset.Custom;
        line.Time = () => custom.AudioSampleTime(1);
        TimeSpan? sampling = null;
        line.Ready = () =>
        {
            if ((custom.Intena & (1 << (InterruptBit.Audio0 + 1))) == 0)
                return false;
            sampling ??= clock.Elapsed;
            return clock.Elapsed - sampling.Value >= TimeSpan.FromSeconds(1);
        };

        ISerialConnection? source = null;
        if (file != null)
        {
            var data = File.ReadAllBytes(file);
            source = new ReplaySerialConnection(data);
            log.WriteLine($"Sending {data.Length} bytes from {file} on the control line of Prevue.");
        }
        else if (port > 0)
        {
            _bridge = new TcpSerialBridge(port, log: log);
            source = _bridge;
            log.WriteLine($"Control line bridge of Prevue on localhost:{_bridge.Port}.");
        }

        Feed = new ControlLineFeed(source);
        line.Connection = Feed;
    }

    /// <summary>The bytes of the line. The requests of /prevue/ctrl add packets to it.</summary>
    public ControlLineFeed Feed { get; }

    public void Dispose() => _bridge?.Dispose();
}
