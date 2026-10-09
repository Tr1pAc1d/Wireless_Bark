using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using AudioBridge.Core.Protocol;

namespace AudioBridge.Core.Session;

public enum ClientMode { WiFi, Usb }

/// <summary>A connected phone. Stats are written by the network threads and read by the UI.</summary>
public abstract class ClientSession
{
    protected ClientSession(string key, ClientMode mode, string remote)
    {
        Key = key;
        Mode = mode;
        Remote = remote;
        Touch();
    }

    public string Key { get; }
    public ClientMode Mode { get; }
    public string Remote { get; }
    public string Name { get; internal set; } = "Android";
    public KeepAliveStats Stats { get; internal set; }
    public double RttMs { get; internal set; } = double.NaN;
    public bool HasStats { get; internal set; }

    private long _lastSeen;
    public TimeSpan SinceLastSeen => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSeen));
    internal void Touch() => Interlocked.Exchange(ref _lastSeen, Stopwatch.GetTimestamp());

    /// <summary>Send one complete message. Must never block the capture thread for long.</summary>
    internal abstract void Send(ReadOnlySpan<byte> message);
    internal virtual void Close() { }
}

internal sealed class UdpClientSession : ClientSession
{
    private readonly Socket _socket;
    private readonly IPEndPoint _ep;

    public UdpClientSession(Socket socket, IPEndPoint ep)
        : base("udp:" + ep, ClientMode.WiFi, ep.ToString())
    {
        _socket = socket;
        _ep = ep;
    }

    internal override void Send(ReadOnlySpan<byte> message)
    {
        try { _socket.SendTo(message, SocketFlags.None, _ep); }
        catch (SocketException) { /* transient (e.g. Wi-Fi blip) - client will time out if it persists */ }
        catch (ObjectDisposedException) { }
    }
}

/// <summary>
/// USB (adb reverse) client. Sends go through a small bounded queue drained by a writer task so a stalled
/// phone can never block audio capture; when full, the oldest packets are dropped (stale audio is useless).
/// </summary>
internal sealed class TcpClientSession : ClientSession
{
    private readonly Socket _socket;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();

    public TcpClientSession(Socket socket, string remote) : base("tcp:" + remote, ClientMode.Usb, remote)
    {
        _socket = socket;
        _ = Task.Run(WriterLoop);
    }

    internal override void Send(ReadOnlySpan<byte> message)
    {
        // Framed: u16 length prefix + message.
        var buf = new byte[message.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)message.Length);
        message.CopyTo(buf.AsSpan(2));
        _queue.Writer.TryWrite(buf);
    }

    private async Task WriterLoop()
    {
        try
        {
            await foreach (var buf in _queue.Reader.ReadAllAsync(_cts.Token))
                await _socket.SendAsync(buf, SocketFlags.None, _cts.Token);
        }
        catch { Close(); }
    }

    internal override void Close()
    {
        _queue.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        try { _socket.Dispose(); } catch { }
    }
}
