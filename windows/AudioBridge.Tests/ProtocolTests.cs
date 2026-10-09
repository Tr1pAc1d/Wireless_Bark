using AudioBridge.Core.Protocol;
using Xunit;

namespace AudioBridge.Tests;

public class ProtocolTests
{
    [Fact]
    public void Header_RoundTrip_PreservesFields()
    {
        Span<byte> buffer = stackalloc byte[Wire.HeaderSize];
        var original = new Header(MessageType.Audio, 42, 12345, 9876543210UL);
        original.Write(buffer);

        Assert.True(Header.TryRead(buffer, out var parsed));
        Assert.Equal(MessageType.Audio, parsed.Type);
        Assert.Equal(42, parsed.Flags);
        Assert.Equal(12345u, parsed.Seq);
        Assert.Equal(9876543210UL, parsed.Value);
    }

    [Fact]
    public void Header_InvalidMagic_ReturnsFalse()
    {
        byte[] buffer = new byte[Wire.HeaderSize];
        buffer[0] = 0x00;
        buffer[1] = 0x00;
        Assert.False(Header.TryRead(buffer, out _));
    }

    [Fact]
    public void Format_RoundTrip_Matches()
    {
        Span<byte> buffer = stackalloc byte[Messages.FormatMessageSize];
        var original = new FormatInfo(48000, 2, 16, 240);
        int written = Messages.WriteFormat(buffer, 7, original);

        Assert.Equal(Messages.FormatMessageSize, written);
        Assert.True(Header.TryRead(buffer, out var h));
        Assert.Equal(MessageType.Format, h.Type);
        Assert.Equal(7u, h.Seq);

        Assert.True(Messages.TryReadFormat(buffer, out var parsed));
        Assert.Equal(48000, parsed.SampleRate);
        Assert.Equal(2, parsed.Channels);
        Assert.Equal(16, parsed.BitsPerSample);
        Assert.Equal(240, parsed.FramesPerPacket);
        Assert.Equal(5.0, parsed.PacketMs);
    }

    [Fact]
    public void Hello_RoundTrip_Matches()
    {
        byte[] buffer = new byte[128];
        var original = new HelloInfo(1, "Pixel 8 Pro");
        int written = Messages.WriteHello(buffer, 1, original);

        Assert.True(Header.TryRead(buffer.AsSpan(0, written), out var h));
        Assert.Equal(MessageType.Hello, h.Type);

        Assert.True(Messages.TryReadHello(buffer.AsSpan(0, written), out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.ProtocolVersion);
        Assert.Equal("Pixel 8 Pro", parsed.DeviceName);
    }

    [Fact]
    public void Discovery_RoundTrip_Matches()
    {
        byte[] buffer = new byte[128];
        var original = new DiscoveryInfo(47800, 47802, 48000, 2, "My Gaming PC");
        int written = Messages.WriteDiscovery(buffer, 1, original);

        Assert.True(Messages.TryReadDiscovery(buffer.AsSpan(0, written), out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(47800, parsed!.UdpPort);
        Assert.Equal(47802, parsed.TcpPort);
        Assert.Equal(48000, parsed.SampleRate);
        Assert.Equal(2, parsed.Channels);
        Assert.Equal("My Gaming PC", parsed.Name);
    }

    [Fact]
    public void KeepAlive_RoundTrip_Matches()
    {
        Span<byte> buffer = stackalloc byte[Messages.KeepAliveMessageSize];
        var original = new KeepAliveStats(15.5f, 20.0f, 10.2f, 1.8f, -12.4f, 1, 2, 0, 1000);
        int written = Messages.WriteKeepAlive(buffer, 9, original);

        Assert.Equal(Messages.KeepAliveMessageSize, written);
        Assert.True(Messages.TryReadKeepAlive(buffer, out var parsed));
        Assert.Equal(15.5f, parsed.BufferMs);
        Assert.Equal(20.0f, parsed.TargetMs);
        Assert.Equal(10.2f, parsed.OutputLatencyMs);
        Assert.Equal(1.8f, parsed.JitterMs);
        Assert.Equal(-12.4f, parsed.DriftPpm);
        Assert.Equal(1u, parsed.PacketsLost);
        Assert.Equal(2u, parsed.PacketsLate);
        Assert.Equal(0u, parsed.Underruns);
        Assert.Equal(1000u, parsed.PacketsReceived);
    }
}
