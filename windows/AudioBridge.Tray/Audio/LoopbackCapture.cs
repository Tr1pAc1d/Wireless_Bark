using System.Runtime.InteropServices;
using AudioBridge.Core.Streaming;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioBridge.Tray.Audio;

/// <summary>
/// Minimal-latency WASAPI loopback capture of one render endpoint.
/// <para>
/// NAudio's stock <c>WasapiLoopbackCapture</c> uses large buffers and coarse polling; this class instead
/// initializes the smallest practical shared-mode buffer and polls every ~1 ms on an MMCSS "Pro Audio"
/// thread, so each engine period (~10 ms) is forwarded the moment it is available.
/// </para>
/// All COM objects are created and used on the capture thread itself (MTA), avoiding cross-apartment marshaling.
/// </summary>
internal sealed class LoopbackCapture : IDisposable
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly string _deviceId;
    private Thread? _thread;
    private volatile bool _stop;

    public LoopbackCapture(string deviceId) => _deviceId = deviceId;

    public delegate void FloatFramesHandler(ReadOnlySpan<float> frames);

    /// <summary>Called once on the capture thread after the stream is initialized (sampleRate, channels).</summary>
    public Action<int, int>? FormatReady;
    /// <summary>Interleaved stereo float frames. Span valid only during the call.</summary>
    public FloatFramesHandler? StereoFrames;
    /// <summary>Endpoint reported a silent buffer of N frames.</summary>
    public Action<int>? SilentFrames;
    /// <summary>Capture stopped because of an error (e.g. device removed).</summary>
    public Action<Exception>? Faulted;

    public string DeviceName { get; private set; } = "";
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    /// <summary>Smoothed size of the chunks WASAPI hands us, in ms (≈ capture latency contribution).</summary>
    public double ChunkMs { get; private set; }
    /// <summary>Peak level since last read (0..1), for silence diagnostics.</summary>
    public float ReadAndResetPeak() => Interlocked.Exchange(ref _peak, 0f);
    private float _peak;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioBridge capture", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private unsafe void Run()
    {
        int taskIndex = 0;
        IntPtr mmcss = NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        NativeMethods.timeBeginPeriod(1);
        AudioClient? client = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(_deviceId);
            DeviceName = device.FriendlyName;
            client = device.AudioClient;
            var fmt = client.MixFormat;
            var kind = Classify(fmt);
            int ch = fmt.Channels;
            SampleRate = fmt.SampleRate;
            Channels = ch;

            // 20 ms buffer (in 100-ns units). Loopback data is delivered once per engine period regardless,
            // so a small buffer only bounds worst-case backlog; it does not add latency.
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback,
                200_000, 0, fmt, Guid.Empty);
            var capture = client.AudioCaptureClient;
            FormatReady?.Invoke(SampleRate, ch);

            var floatBuf = new float[SampleRate * ch];      // 1 s headroom
            var stereoBuf = new float[SampleRate * 2];
            client.Start();

            while (!_stop)
            {
                int next = capture.GetNextPacketSize();
                if (next == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }
                while (next > 0 && !_stop)
                {
                    IntPtr ptr = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    if (frames > 0)
                    {
                        if ((flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            SilentFrames?.Invoke(frames);
                        }
                        else
                        {
                            int n = frames * ch;
                            if (n > floatBuf.Length) { floatBuf = new float[n]; stereoBuf = new float[frames * 2]; }
                            Convert(ptr, kind, n, floatBuf);
                            Downmix.ToStereo(floatBuf, ch, frames, stereoBuf);
                            var span = new ReadOnlySpan<float>(stereoBuf, 0, frames * 2);
                            TrackPeak(span);
                            StereoFrames?.Invoke(span);
                        }
                        double ms = frames * 1000.0 / SampleRate;
                        ChunkMs = ChunkMs == 0 ? ms : ChunkMs * 0.95 + ms * 0.05;
                    }
                    capture.ReleaseBuffer(frames);
                    next = capture.GetNextPacketSize();
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            if (!_stop) Faulted?.Invoke(ex);
        }
        finally
        {
            try { client?.Dispose(); } catch { }
            NativeMethods.timeEndPeriod(1);
            if (mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    private void TrackPeak(ReadOnlySpan<float> s)
    {
        float p = 0;
        for (int i = 0; i < s.Length; i += 8) { float a = MathF.Abs(s[i]); if (a > p) p = a; }
        if (p > _peak) _peak = p;
    }

    private enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

    private static SampleKind Classify(WaveFormat fmt)
    {
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == SubtypeIeeeFloat);
        if (isFloat && fmt.BitsPerSample == 32) return SampleKind.Float32;
        bool isPcm = fmt.Encoding == WaveFormatEncoding.Pcm ||
                     (fmt is WaveFormatExtensible ext2 && ext2.SubFormat == SubtypePcm);
        if (isPcm)
        {
            return fmt.BitsPerSample switch
            {
                16 => SampleKind.Pcm16,
                24 => SampleKind.Pcm24,
                32 => SampleKind.Pcm32,
                _ => throw new NotSupportedException($"Unsupported PCM bit depth {fmt.BitsPerSample}"),
            };
        }
        throw new NotSupportedException($"Unsupported mix format {fmt}");
    }

    private static unsafe void Convert(IntPtr src, SampleKind kind, int samples, float[] dst)
    {
        switch (kind)
        {
            case SampleKind.Float32:
                new ReadOnlySpan<float>((void*)src, samples).CopyTo(dst);
                break;
            case SampleKind.Pcm16:
            {
                var s = new ReadOnlySpan<short>((void*)src, samples);
                for (int i = 0; i < samples; i++) dst[i] = s[i] / 32768f;
                break;
            }
            case SampleKind.Pcm24:
            {
                byte* b = (byte*)src;
                for (int i = 0; i < samples; i++, b += 3)
                    dst[i] = ((b[0] << 8 | b[1] << 16 | b[2] << 24) >> 8) / 8388608f;
                break;
            }
            case SampleKind.Pcm32:
            {
                var s = new ReadOnlySpan<int>((void*)src, samples);
                for (int i = 0; i < samples; i++) dst[i] = s[i] / 2147483648f;
                break;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(1000);
    }
}
