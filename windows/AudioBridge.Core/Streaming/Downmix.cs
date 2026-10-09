namespace AudioBridge.Core.Streaming;

/// <summary>Converts any channel layout to interleaved stereo.</summary>
public static class Downmix
{
    private const float M3dB = 0.70710678f;

    /// <summary>
    /// Downmix <paramref name="frames"/> frames of <paramref name="channels"/>-channel interleaved audio
    /// into <paramref name="dstStereo"/> (length >= frames*2). Assumes standard WAVE channel order
    /// (FL, FR, FC, LFE, BL, BR, SL, SR). LFE is dropped.
    /// </summary>
    public static void ToStereo(ReadOnlySpan<float> src, int channels, int frames, Span<float> dstStereo)
    {
        switch (channels)
        {
            case 2:
                src[..(frames * 2)].CopyTo(dstStereo);
                return;
            case 1:
                for (int f = 0; f < frames; f++)
                {
                    float v = src[f];
                    dstStereo[2 * f] = v;
                    dstStereo[2 * f + 1] = v;
                }
                return;
        }

        for (int f = 0; f < frames; f++)
        {
            var fr = src.Slice(f * channels, channels);
            float l = fr[0];
            float r = fr[1];
            if (channels >= 3) { l += M3dB * fr[2]; r += M3dB * fr[2]; }
            // fr[3] = LFE (dropped)
            if (channels >= 6) { l += M3dB * fr[4]; r += M3dB * fr[5]; }
            if (channels >= 8) { l += M3dB * fr[6]; r += M3dB * fr[7]; }
            dstStereo[2 * f] = l;
            dstStereo[2 * f + 1] = r;
        }
    }
}
