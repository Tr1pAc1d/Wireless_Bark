using System.Buffers.Binary;
using System.Text;

namespace AudioBridge.Core.Protocol;

/// <summary>Message type byte at offset 2 of every message. See docs/protocol.md.</summary>
public enum MessageType : byte
{
    Discovery = 1,
    Hello = 2,
    Format = 3,
    Audio = 4,
    KeepAlive = 5,
    Ping = 6,
    Pong = 7,
    Bye = 8,
}

/// <summary>Protocol-wide constants.</summary>
public static class Wire
{
    public const ushort Magic = 0xAB01;
    public const int HeaderSize = 16;
    public const ushort ProtocolVersion = 1;

    public const int UdpPort = 47800;
    public const int DiscoveryPort = 47801;
    public const int TcpPort = 47802;

    /// <summary>Upper bound for any single message (keeps UDP datagrams un-fragmented).</summary>
    public const int MaxMessageSize = 1400;
    public const int MaxNameBytes = 64;
}

/// <summary>16-byte common header.</summary>
public readonly record struct Header(MessageType Type, byte Flags, uint Seq, ulong Value)
{
    public void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dst, Wire.Magic);
        dst[2] = (byte)Type;
        dst[3] = Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], Seq);
        BinaryPrimitives.WriteUInt64LittleEndian(dst[8..], Value);
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out Header header)
    {
        header = default;
        if (src.Length < Wire.HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(src) != Wire.Magic) return false;
        header = new Header(
            (MessageType)src[2],
            src[3],
            BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(src[8..]));
        return true;
    }
}

public readonly record struct FormatInfo(int SampleRate, int Channels, int BitsPerSample, int FramesPerPacket)
{
    public double PacketMs => FramesPerPacket * 1000.0 / SampleRate;
    public int AudioMessageSize => Wire.HeaderSize + FramesPerPacket * Channels * (BitsPerSample / 8);
}

public sealed record DiscoveryInfo(int UdpPort, int TcpPort, int SampleRate, int Channels, string Name);

public sealed record HelloInfo(int ProtocolVersion, string DeviceName);

public readonly record struct KeepAliveStats(
    float BufferMs,
    float TargetMs,
    float OutputLatencyMs,
    float JitterMs,
    float DriftPpm,
    uint PacketsLost,
    uint PacketsLate,
    uint Underruns,
    uint PacketsReceived);

/// <summary>Allocation-free encoders/decoders for every message type.</summary>
public static class Messages
{
    // ---- simple header-only messages -------------------------------------------------

    public static int WriteHeaderOnly(Span<byte> dst, MessageType type, uint seq, ulong value)
    {
        new Header(type, 0, seq, value).Write(dst);
        return Wire.HeaderSize;
    }

    // ---- DISCOVERY -------------------------------------------------------------------

    public static int WriteDiscovery(Span<byte> dst, uint seq, DiscoveryInfo info)
    {
        new Header(MessageType.Discovery, 0, seq, 0).Write(dst);
        var p = dst[Wire.HeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)info.UdpPort);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], (ushort)info.TcpPort);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], (uint)info.SampleRate);
        p[8] = (byte)info.Channels;
        int n = WriteName(p[9..], info.Name);
        return Wire.HeaderSize + 9 + n;
    }

    public static bool TryReadDiscovery(ReadOnlySpan<byte> msg, out DiscoveryInfo? info)
    {
        info = null;
        var p = msg[Wire.HeaderSize..];
        if (p.Length < 10) return false;
        if (!TryReadName(p[9..], out var name)) return false;
        info = new DiscoveryInfo(
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(p[4..]),
            p[8],
            name);
        return true;
    }

    // ---- HELLO -----------------------------------------------------------------------

    public static int WriteHello(Span<byte> dst, uint seq, HelloInfo info)
    {
        new Header(MessageType.Hello, 0, seq, 0).Write(dst);
        var p = dst[Wire.HeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)info.ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], 0);
        int n = WriteName(p[4..], info.DeviceName);
        return Wire.HeaderSize + 4 + n;
    }

    public static bool TryReadHello(ReadOnlySpan<byte> msg, out HelloInfo? info)
    {
        info = null;
        var p = msg[Wire.HeaderSize..];
        if (p.Length < 5) return false;
        if (!TryReadName(p[4..], out var name)) return false;
        info = new HelloInfo(BinaryPrimitives.ReadUInt16LittleEndian(p), name);
        return true;
    }

    // ---- FORMAT ----------------------------------------------------------------------

    public const int FormatMessageSize = Wire.HeaderSize + 8;

    public static int WriteFormat(Span<byte> dst, uint seq, FormatInfo f)
    {
        new Header(MessageType.Format, 0, seq, 0).Write(dst);
        var p = dst[Wire.HeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)f.SampleRate);
        p[4] = (byte)f.Channels;
        p[5] = (byte)f.BitsPerSample;
        BinaryPrimitives.WriteUInt16LittleEndian(p[6..], (ushort)f.FramesPerPacket);
        return FormatMessageSize;
    }

    public static bool TryReadFormat(ReadOnlySpan<byte> msg, out FormatInfo f)
    {
        f = default;
        if (msg.Length < FormatMessageSize) return false;
        var p = msg[Wire.HeaderSize..];
        f = new FormatInfo(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(p),
            p[4],
            p[5],
            BinaryPrimitives.ReadUInt16LittleEndian(p[6..]));
        return true;
    }

    // ---- KEEPALIVE -------------------------------------------------------------------

    public const int KeepAliveMessageSize = Wire.HeaderSize + 36;

    public static int WriteKeepAlive(Span<byte> dst, uint seq, KeepAliveStats s)
    {
        new Header(MessageType.KeepAlive, 0, seq, 0).Write(dst);
        var p = dst[Wire.HeaderSize..];
        BinaryPrimitives.WriteSingleLittleEndian(p, s.BufferMs);
        BinaryPrimitives.WriteSingleLittleEndian(p[4..], s.TargetMs);
        BinaryPrimitives.WriteSingleLittleEndian(p[8..], s.OutputLatencyMs);
        BinaryPrimitives.WriteSingleLittleEndian(p[12..], s.JitterMs);
        BinaryPrimitives.WriteSingleLittleEndian(p[16..], s.DriftPpm);
        BinaryPrimitives.WriteUInt32LittleEndian(p[20..], s.PacketsLost);
        BinaryPrimitives.WriteUInt32LittleEndian(p[24..], s.PacketsLate);
        BinaryPrimitives.WriteUInt32LittleEndian(p[28..], s.Underruns);
        BinaryPrimitives.WriteUInt32LittleEndian(p[32..], s.PacketsReceived);
        return KeepAliveMessageSize;
    }

    public static bool TryReadKeepAlive(ReadOnlySpan<byte> msg, out KeepAliveStats s)
    {
        s = default;
        if (msg.Length < KeepAliveMessageSize) return false;
        var p = msg[Wire.HeaderSize..];
        s = new KeepAliveStats(
            BinaryPrimitives.ReadSingleLittleEndian(p),
            BinaryPrimitives.ReadSingleLittleEndian(p[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[8..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[12..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[32..]));
        return true;
    }

    // ---- helpers ---------------------------------------------------------------------

    private static int WriteName(Span<byte> dst, string name)
    {
        // Truncate on a UTF-8 boundary-safe basis by encoding then trimming chars.
        var bytes = Encoding.UTF8.GetBytes(name);
        int len = Math.Min(bytes.Length, Wire.MaxNameBytes);
        while (len > 0 && len < bytes.Length && (bytes[len] & 0xC0) == 0x80) len--;
        dst[0] = (byte)len;
        bytes.AsSpan(0, len).CopyTo(dst[1..]);
        return 1 + len;
    }

    private static bool TryReadName(ReadOnlySpan<byte> src, out string name)
    {
        name = "";
        if (src.Length < 1) return false;
        int len = src[0];
        if (src.Length < 1 + len) return false;
        name = Encoding.UTF8.GetString(src.Slice(1, len));
        return true;
    }
}
