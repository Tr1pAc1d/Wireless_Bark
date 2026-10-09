using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AudioBridge.Core.Protocol;

namespace AudioBridge.Core.Session;

public sealed class StreamServerOptions
{
    public int UdpPort { get; init; } = Wire.UdpPort;
    public int TcpPort { get; init; } = Wire.TcpPort;
    public int DiscoveryPort { get; init; } = Wire.DiscoveryPort;
    public string ServerName { get; init; } = Environment.MachineName;
    public bool EnableTcp { get; init; } = true;
    public bool EnableDiscovery { get; init; } = true;
    public TimeSpan ClientTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan HousekeepingInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Accepts phones over UDP (Wi-Fi) and TCP (USB via adb reverse), answers HELLO with FORMAT,
/// fans AUDIO packets out to every client, pings for RTT, broadcasts discovery beacons and
/// expires silent clients.
/// </summary>
public sealed class StreamServer : IDisposable
{
    private const int SIO_UDP_CONNRESET = -1744830452;

    private readonly StreamServerOptions _opt;
    private readonly ConcurrentDictionary<string, ClientSession> _clients = new();
    private volatile ClientSession[] _snapshot = Array.Empty<ClientSession>();
    private readonly object _formatLock = new();
    private readonly CancellationTokenSource _cts = new();

    private Socket? _udp;
    private Socket? _tcpListener;
    private Thread? _udpThread;
    private Timer? _housekeeping;
    private uint _ctrlSeq;
    private FormatInfo? _format;

    public StreamServer(StreamServerOptions? options = null) => _opt = options ?? new StreamServerOptions();

    public int UdpPort { get; private set; }
    public int TcpPort { get; private set; }
    public FormatInfo? Format { get { lock (_formatLock) return _format; } }
    public IReadOnlyList<ClientSession> Clients => _snapshot;
    public int ClientCount => _snapshot.Length;

    /// <summary>Raised (on a network thread) when the number of clients changes.</summary>
    public event Action? ClientsChanged;

    public void Start()
    {
        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // Don't let ICMP "port unreachable" from a vanished phone kill the receive loop.
            _udp.IOControl(SIO_UDP_CONNRESET, new byte[4], null);
        }
        _udp.EnableBroadcast = true;
        _udp.SendBufferSize = 64 * 1024;
        _udp.Bind(new IPEndPoint(IPAddress.Any, _opt.UdpPort));
        UdpPort = ((IPEndPoint)_udp.LocalEndPoint!).Port;
        // Mark audio as voice-class traffic (DSCP EF) - respected by many Wi-Fi APs (WMM).
        try { _udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8); } catch { }

        _udpThread = new Thread(UdpReceiveLoop) { IsBackground = true, Name = "AudioBridge UDP rx" };
        _udpThread.Start();

        if (_opt.EnableTcp)
        {
            _tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _tcpListener.Bind(new IPEndPoint(IPAddress.Loopback, _opt.TcpPort));
            _tcpListener.Listen(4);
            TcpPort = ((IPEndPoint)_tcpListener.LocalEndPoint!).Port;
            _ = Task.Run(TcpAcceptLoop);
        }

        _housekeeping = new Timer(_ => Housekeeping(), null, TimeSpan.Zero, _opt.HousekeepingInterval);
    }

    /// <summary>Set/replace the stream format and announce it to every client.</summary>
    public void SetFormat(FormatInfo format)
    {
        lock (_formatLock) _format = format;
        foreach (var c in _snapshot) SendFormat(c);
    }

    /// <summary>Send one AUDIO message to every client. Called from the capture thread.</summary>
    public void SendAudio(ReadOnlySpan<byte> message)
    {
        var clients = _snapshot;
        for (int i = 0; i < clients.Length; i++) clients[i].Send(message);
    }

    // ------------------------------------------------------------------------------------
    // Receive paths
    // ------------------------------------------------------------------------------------

    private void UdpReceiveLoop()
    {
        var buf = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            int n;
            EndPoint from = any;
            try { n = _udp!.ReceiveFrom(buf, ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }

            var ep = (IPEndPoint)from;
            var msg = buf.AsSpan(0, n);
            if (!Header.TryRead(msg, out var h)) continue;
            string key = "udp:" + ep;

            if (h.Type == MessageType.Hello)
            {
                var session = _clients.GetOrAdd(key, _ => new UdpClientSession(_udp!, ep));
                OnHello(session, msg);
            }
            else if (_clients.TryGetValue(key, out var session))
            {
                HandleControl(session, h, msg);
            }
        }
    }

    private async Task TcpAcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try { s = await _tcpListener!.AcceptAsync(_cts.Token); }
            catch { return; }
            s.NoDelay = true;
            s.SendBufferSize = 32 * 1024;
            _ = Task.Run(() => TcpClientLoop(s));
        }
    }

    private async Task TcpClientLoop(Socket s)
    {
        var remote = s.RemoteEndPoint?.ToString() ?? "usb";
        TcpClientSession? session = null;
        var lenBuf = new byte[2];
        var buf = new byte[Wire.MaxMessageSize + 64];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!await ReadExact(s, lenBuf)) break;
                int len = BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);
                if (len < Wire.HeaderSize || len > buf.Length) break;
                if (!await ReadExact(s, buf.AsMemory(0, len))) break;
                if (!Header.TryRead(buf.AsSpan(0, len), out var h)) break;

                if (h.Type == MessageType.Hello)
                {
                    if (session == null)
                    {
                        session = new TcpClientSession(s, remote);
                        _clients[session.Key] = session;
                    }
                    OnHello(session, buf.AsSpan(0, len));
                }
                else if (session != null)
                {
                    HandleControl(session, h, buf.AsSpan(0, len));
                }
            }
        }
        catch { /* connection dropped */ }
        finally
        {
            if (session != null) Remove(session);
            else s.Dispose();
        }
    }

    private static async Task<bool> ReadExact(Socket s, Memory<byte> dst)
    {
        int got = 0;
        while (got < dst.Length)
        {
            int n = await s.ReceiveAsync(dst[got..], SocketFlags.None);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    // ------------------------------------------------------------------------------------
    // Message handling
    // ------------------------------------------------------------------------------------

    private void OnHello(ClientSession session, ReadOnlySpan<byte> msg)
    {
        session.Touch();
        if (Messages.TryReadHello(msg, out var hello) && hello != null && hello.DeviceName.Length > 0)
            session.Name = hello.DeviceName;
        RebuildSnapshot();
        SendFormat(session);
    }

    private void HandleControl(ClientSession session, Header h, ReadOnlySpan<byte> msg)
    {
        session.Touch();
        switch (h.Type)
        {
            case MessageType.KeepAlive:
                if (Messages.TryReadKeepAlive(msg, out var stats))
                {
                    session.Stats = stats;
                    session.HasStats = true;
                }
                break;
            case MessageType.Pong:
                long ticks = Stopwatch.GetTimestamp() - (long)h.Value;
                if (ticks >= 0) session.RttMs = ticks * 1000.0 / Stopwatch.Frequency;
                break;
            case MessageType.Bye:
                Remove(session);
                break;
        }
    }

    private void SendFormat(ClientSession c)
    {
        var f = Format;
        if (f == null) return;
        Span<byte> buf = stackalloc byte[Messages.FormatMessageSize];
        Messages.WriteFormat(buf, Interlocked.Increment(ref _ctrlSeq), f.Value);
        c.Send(buf);
    }

    // ------------------------------------------------------------------------------------
    // Housekeeping: ping, discovery, timeouts
    // ------------------------------------------------------------------------------------

    private void Housekeeping()
    {
        if (_cts.IsCancellationRequested) return;
        try
        {
            Span<byte> ping = stackalloc byte[Wire.HeaderSize];
            foreach (var c in _snapshot)
            {
                if (c.SinceLastSeen > _opt.ClientTimeout) { Remove(c); continue; }
                Messages.WriteHeaderOnly(ping, MessageType.Ping, Interlocked.Increment(ref _ctrlSeq),
                    (ulong)Stopwatch.GetTimestamp());
                c.Send(ping);
            }
            if (_opt.EnableDiscovery) BroadcastDiscovery();
        }
        catch { /* never let the timer die */ }
    }

    private void BroadcastDiscovery()
    {
        var f = Format;
        var info = new DiscoveryInfo(UdpPort, TcpPort, f?.SampleRate ?? 0, f?.Channels ?? 2, _opt.ServerName);
        Span<byte> buf = stackalloc byte[Wire.MaxMessageSize];
        int n = Messages.WriteDiscovery(buf, Interlocked.Increment(ref _ctrlSeq), info);
        var msg = buf[..n].ToArray();

        foreach (var target in BroadcastAddresses())
        {
            try { _udp!.SendTo(msg, new IPEndPoint(target, _opt.DiscoveryPort)); }
            catch { }
        }
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                result.Add(new IPAddress(b));
            }
        }
        return result;
    }

    private void Remove(ClientSession c)
    {
        if (_clients.TryRemove(c.Key, out _))
        {
            c.Close();
            RebuildSnapshot();
        }
    }

    private void RebuildSnapshot()
    {
        var before = _snapshot.Length;
        _snapshot = _clients.Values.ToArray();
        if (before != _snapshot.Length) ClientsChanged?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _housekeeping?.Dispose();
        Span<byte> bye = stackalloc byte[Wire.HeaderSize];
        Messages.WriteHeaderOnly(bye, MessageType.Bye, 0, 0);
        foreach (var c in _snapshot)
        {
            c.Send(bye);
            c.Close();
        }
        _clients.Clear();
        _snapshot = Array.Empty<ClientSession>();
        try { _udp?.Dispose(); } catch { }
        try { _tcpListener?.Dispose(); } catch { }
    }
}
