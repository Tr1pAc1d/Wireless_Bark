using System.Buffers.Binary;
using AudioBridge.Core.Protocol;
using AudioBridge.Core.Streaming;
using Xunit;

namespace AudioBridge.Tests;

public class PacketizerTests
{
    [Fact]
    public void Packetizer_EmitsExactPackets_AtCorrectCadence()
    {
        var packets = new List<byte[]>();
        var packetizer = new Packetizer(48000, 240, msg => packets.Add(msg.ToArray()));

        Assert.Equal(5.0, packetizer.Format.PacketMs);
        Assert.Equal(Wire.HeaderSize + 240 * 2 * 2, packetizer.Format.AudioMessageSize);

        // Push 120 frames (half packet) -> should not emit yet
        float[] half = new float[120 * 2];
        packetizer.PushStereo(half);
        Assert.Empty(packets);

        // Push another 120 frames -> exactly 1 packet emitted
        packetizer.PushStereo(half);
        Assert.Single(packets);

        // Verify packet header
        Assert.True(Header.TryRead(packets[0], out var h));
        Assert.Equal(MessageType.Audio, h.Type);
        Assert.Equal(0u, h.Seq);
        Assert.Equal(0UL, h.Value); // first sample index is 0

        // Push 480 frames (2 full packets)
        float[] twoPackets = new float[480 * 2];
        packetizer.PushStereo(twoPackets);
        Assert.Equal(3, packets.Count);

        Assert.True(Header.TryRead(packets[1], out var h1));
        Assert.Equal(1u, h1.Seq);
        Assert.Equal(240UL, h1.Value); // sample index advanced by 240

        Assert.True(Header.TryRead(packets[2], out var h2));
        Assert.Equal(2u, h2.Seq);
        Assert.Equal(480UL, h2.Value);
    }

    [Fact]
    public void Packetizer_ClampsAndConvertsFloatToInt16()
    {
        byte[]? lastPacket = null;
        var packetizer = new Packetizer(48000, 4, msg => lastPacket = msg.ToArray());

        // 4 frames stereo: test 1.0 (max), -1.0 (min), overshoot (+2.0, -2.0), 0.0
        float[] test = new float[]
        {
            1.0f, -1.0f,
            2.0f, -2.0f,
            0.0f, 0.5f,
            -0.5f, 0.0f
        };
        packetizer.PushStereo(test);

        Assert.NotNull(lastPacket);
        var pcm = lastPacket.AsSpan(Wire.HeaderSize);

        short s0 = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(0, 2));
        short s1 = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(2, 2));
        short s2 = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(4, 2));
        short s3 = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(6, 2));

        Assert.Equal(32767, s0);
        Assert.Equal(-32767, s1);
        Assert.Equal(32767, s2); // clamped
        Assert.Equal(-32768, s3); // clamped
    }

    [Fact]
    public void Downmix_StereoPassThrough_Unmodified()
    {
        float[] src = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        float[] dst = new float[4];
        Downmix.ToStereo(src, 2, 2, dst);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void Downmix_Mono_DuplicatesToBothChannels()
    {
        float[] src = new float[] { 0.5f, -0.25f };
        float[] dst = new float[4];
        Downmix.ToStereo(src, 1, 2, dst);
        Assert.Equal(new float[] { 0.5f, 0.5f, -0.25f, -0.25f }, dst);
    }
}
