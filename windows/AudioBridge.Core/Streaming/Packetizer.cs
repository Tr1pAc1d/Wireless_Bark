using System.Buffers.Binary;
using AudioBridge.Core.Protocol;

namespace AudioBridge.Core.Streaming;

/// <summary>Receives one complete wire message (header + payload). The span is only valid during the call.</summary>
public delegate void MessageHandler(ReadOnlySpan<byte> message);

/// <summary>
/// Turns a continuous stream of interleaved stereo float frames into fixed-size AUDIO messages
/// (int16 PCM). A packet is emitted the instant it is full - there is no batching or timer.
/// Not thread-safe: call from a single capture thread.
/// </summary>
public sealed class Packetizer
{
    public const int Channels = 2;
    public const int BitsPerSample = 16;

    private readonly MessageHandler _onPacket;
    private readonly byte[] _msg;
    private int _fillFrames;
    private uint _seq;

    public Packetizer(int sampleRate, int framesPerPacket, MessageHandler onPacket)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (framesPerPacket <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerPacket));
        Format = new FormatInfo(sampleRate, Channels, BitsPerSample, framesPerPacket);
        if (Format.AudioMessageSize > Wire.MaxMessageSize)
            throw new ArgumentOutOfRangeException(nameof(framesPerPacket), "Packet would exceed the max message size.");
        _onPacket = onPacket;
        _msg = new byte[Format.AudioMessageSize];
    }

    public FormatInfo Format { get; }

    /// <summary>Frame index of the first frame of the packet currently being filled.</summary>
    public ulong SampleIndex { get; private set; }

    public uint PacketsSent => _seq;

    /// <summary>5 ms worth of frames at the given rate (240 @ 48 kHz, 220 @ 44.1 kHz).</summary>
    public static int DefaultFramesPerPacket(int sampleRate) => Math.Max(1, sampleRate / 200);

    /// <summary>Push interleaved stereo float samples in [-1, 1]. Length must be even.</summary>
    public void PushStereo(ReadOnlySpan<float> interleaved)
    {
        int fpp = Format.FramesPerPacket;
        int i = 0;
        int frames = interleaved.Length / Channels;
        while (i < frames)
        {
            int take = Math.Min(fpp - _fillFrames, frames - i);
            var dst = _msg.AsSpan(Wire.HeaderSize + _fillFrames * Channels * 2, take * Channels * 2);
            var src = interleaved.Slice(i * Channels, take * Channels);
            for (int s = 0; s < src.Length; s++)
            {
                float v = src[s] * 32767f;
                if (v > 32767f) v = 32767f;
                else if (v < -32768f) v = -32768f;
                BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(s * 2, 2), (short)MathF.Round(v));
            }
            _fillFrames += take;
            i += take;
            if (_fillFrames == fpp) Flush();
        }
    }

    /// <summary>Push digital silence (used when the endpoint reports a silent buffer).</summary>
    public void PushSilence(int frames)
    {
        int fpp = Format.FramesPerPacket;
        while (frames > 0)
        {
            int take = Math.Min(fpp - _fillFrames, frames);
            _msg.AsSpan(Wire.HeaderSize + _fillFrames * Channels * 2, take * Channels * 2).Clear();
            _fillFrames += take;
            frames -= take;
            if (_fillFrames == fpp) Flush();
        }
    }

    private void Flush()
    {
        new Header(MessageType.Audio, 0, _seq++, SampleIndex).Write(_msg);
        _onPacket(_msg);
        SampleIndex += (ulong)Format.FramesPerPacket;
        _fillFrames = 0;
    }
}
