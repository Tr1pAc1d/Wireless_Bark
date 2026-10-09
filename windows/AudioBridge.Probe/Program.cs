using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AudioBridge.Core.Protocol;

namespace AudioBridge.Probe;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "127.0.0.1";
        int port = args.Length > 1 ? int.Parse(args[1]) : Wire.UdpPort;
        bool isTcp = args.Contains("--tcp");

        Console.WriteLine("AudioBridge Probe (Test Receiver)");
        Console.WriteLine($"Target: {host}:{port} (Mode: {(isTcp ? "TCP" : "UDP")})");
        Console.WriteLine("Press Ctrl+C to exit.\n");

        if (isTcp)
        {
            await RunTcp(host, port);
        }
        else
        {
            RunUdp(host, port);
        }
    }

    private static void RunUdp(string host, int port)
    {
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        var target = new IPEndPoint(IPAddress.Parse(host), port);

        // Send HELLO
        byte[] hello = new byte[64];
        int len = Messages.WriteHello(hello, 1, new HelloInfo(1, "ProbeReceiver"));
        udp.SendTo(hello, 0, len, SocketFlags.None, target);
        Console.WriteLine("Sent HELLO to PC...");

        byte[] buf = new byte[2048];
        byte[] pong = new byte[Wire.HeaderSize];
        EndPoint ep = target;

        long packetsReceived = 0;
        long packetsLost = 0;
        uint? lastSeq = null;
        var sw = Stopwatch.StartNew();
        long lastReportTime = 0;
        long lastReportPackets = 0;
        double lastArrival = 0;
        double jitterSum = 0;
        long jitterCount = 0;

        using var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            byte[] ka = new byte[Messages.KeepAliveMessageSize];
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(500, cts.Token);
                Messages.WriteKeepAlive(ka, 1, new KeepAliveStats(
                    BufferMs: 15.0f,
                    TargetMs: 15.0f,
                    OutputLatencyMs: 10.0f,
                    JitterMs: (float)(jitterCount > 0 ? jitterSum / jitterCount : 0),
                    DriftPpm: 0,
                    PacketsLost: (uint)packetsLost,
                    PacketsLate: 0,
                    Underruns: 0,
                    PacketsReceived: (uint)packetsReceived
                ));
                try { udp.SendTo(ka, target); } catch { }
            }
        }, cts.Token);

        while (true)
        {
            int n = udp.ReceiveFrom(buf, ref ep);
            double now = sw.Elapsed.TotalMilliseconds;
            if (lastArrival > 0)
            {
                double delta = now - lastArrival;
                double jitter = Math.Abs(delta - 5.0); // nominal 5ms
                jitterSum += jitter;
                jitterCount++;
            }
            lastArrival = now;

            var msg = buf.AsSpan(0, n);
            if (!Header.TryRead(msg, out var h)) continue;

            if (h.Type == MessageType.Format)
            {
                if (Messages.TryReadFormat(msg, out var fmt))
                {
                    Console.WriteLine($"[FORMAT RECEIVED] {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit, {fmt.FramesPerPacket} fpp ({fmt.PacketMs:0.#}ms)");
                }
            }
            else if (h.Type == MessageType.Ping)
            {
                Messages.WriteHeaderOnly(pong, MessageType.Pong, h.Seq, h.Value);
                udp.SendTo(pong, target);
            }
            else if (h.Type == MessageType.Audio)
            {
                packetsReceived++;
                if (lastSeq.HasValue)
                {
                    uint diff = h.Seq - lastSeq.Value;
                    if (diff > 1) packetsLost += (diff - 1);
                }
                lastSeq = h.Seq;
            }

            if (now - lastReportTime >= 1000)
            {
                long pps = packetsReceived - lastReportPackets;
                double avgJitter = jitterCount > 0 ? jitterSum / jitterCount : 0;
                Console.WriteLine($"Rx: {pps} pkts/s (Total: {packetsReceived}) | Lost: {packetsLost} | Jitter: {avgJitter:0.00} ms");
                lastReportTime = (long)now;
                lastReportPackets = packetsReceived;
                jitterSum = 0;
                jitterCount = 0;
            }
        }
    }

    private static async Task RunTcp(string host, int port)
    {
        using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await tcp.ConnectAsync(host, port);
        tcp.NoDelay = true;
        Console.WriteLine("Connected to PC over TCP...");

        byte[] hello = new byte[64];
        int bodyLen = Messages.WriteHello(hello.AsSpan(2), 1, new HelloInfo(1, "ProbeReceiverTcp"));
        BinaryPrimitives.WriteUInt16LittleEndian(hello, (ushort)bodyLen);
        await tcp.SendAsync(hello.AsMemory(0, bodyLen + 2), SocketFlags.None);

        byte[] lenBuf = new byte[2];
        byte[] buf = new byte[2048];
        long packetsReceived = 0;
        var sw = Stopwatch.StartNew();
        long lastReportTime = 0;
        long lastReportPackets = 0;

        while (true)
        {
            if (!await ReadExact(tcp, lenBuf)) break;
            int len = BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);
            if (!await ReadExact(tcp, buf.AsMemory(0, len))) break;

            if (Header.TryRead(buf.AsSpan(0, len), out var h))
            {
                if (h.Type == MessageType.Audio) packetsReceived++;
                else if (h.Type == MessageType.Format && Messages.TryReadFormat(buf.AsSpan(0, len), out var fmt))
                {
                    Console.WriteLine($"[FORMAT RECEIVED] {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.FramesPerPacket} fpp");
                }
            }

            long now = sw.ElapsedMilliseconds;
            if (now - lastReportTime >= 1000)
            {
                Console.WriteLine($"TCP Rx: {packetsReceived - lastReportPackets} pkts/s (Total: {packetsReceived})");
                lastReportTime = now;
                lastReportPackets = packetsReceived;
            }
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
}
