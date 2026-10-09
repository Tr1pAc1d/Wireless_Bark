#pragma once

#include "jitter_buffer.h"
#include <vector>
#include <cmath>
#include <algorithm>

namespace audiobridge {

class DriftResampler {
public:
    DriftResampler() = default;

    void reset() {
        mIntegral = 0.0f;
        mCurrentRatio = 1.0f;
        mPhase = 0.0;
        mHistory[0] = mHistory[1] = mHistory[2] = mHistory[3] = 0.0f;
        mHistory[4] = mHistory[5] = mHistory[6] = mHistory[7] = 0.0f;
    }

    void setTargetMs(float targetMs) {
        mTargetMs = std::max(5.0f, std::min(100.0f, targetMs));
    }

    float getDriftPpm() const {
        return (mCurrentRatio - 1.0f) * 1e6f;
    }

    // Called on audio thread in onAudioReady
    void render(JitterBuffer& jitter, float* outStereo, size_t outFrames, uint32_t sampleRate) {
        // PI controller update
        float currentFillMs = jitter.getBufferFillMs(sampleRate);
        float error = currentFillMs - mTargetMs; // positive if buffer has too much audio

        constexpr float Kp = 0.00005f; // proportional gain
        constexpr float Ki = 0.000001f; // integral gain

        mIntegral += error;
        mIntegral = std::max(-500.0f, std::min(500.0f, mIntegral));

        // If buffer is too full, speed up slightly (ratio > 1.0); if too empty, slow down (ratio < 1.0)
        float adjustment = (Kp * error) + (Ki * mIntegral);
        adjustment = std::max(-0.005f, std::min(0.005f, adjustment)); // clamp to +/- 0.5%
        mCurrentRatio = 1.0f + adjustment;

        // Render frames using jitter buffer
        // For real-time simplicity and zero artifact at 1.0 ratio:
        if (std::abs(mCurrentRatio - 1.0f) < 0.0001f && mPhase == 0.0) {
            jitter.pullFrames(outStereo, outFrames);
            return;
        }

        // Fractional interpolation pull
        for (size_t i = 0; i < outFrames; ++i) {
            while (mPhase >= 1.0) {
                // Shift history
                mHistory[0] = mHistory[2]; mHistory[1] = mHistory[3];
                mHistory[2] = mHistory[4]; mHistory[3] = mHistory[5];
                mHistory[4] = mHistory[6]; mHistory[5] = mHistory[7];

                float nextSample[2] = {0.0f, 0.0f};
                jitter.pullFrames(nextSample, 1);
                mHistory[6] = nextSample[0];
                mHistory[7] = nextSample[1];
                mPhase -= 1.0;
            }

            // Cubic Hermite interpolation
            float mu = static_cast<float>(mPhase);
            outStereo[i * 2] = interpolateCubic(mHistory[0], mHistory[2], mHistory[4], mHistory[6], mu);
            outStereo[i * 2 + 1] = interpolateCubic(mHistory[1], mHistory[3], mHistory[5], mHistory[7], mu);

            mPhase += mCurrentRatio;
        }
    }

private:
    static inline float interpolateCubic(float y0, float y1, float y2, float y3, float mu) {
        float a0, a1, a2, a3, mu2;
        mu2 = mu * mu;
        a0 = y3 - y2 - y0 + y1;
        a1 = y0 - y1 - a0;
        a2 = y2 - y0;
        a3 = y1;
        return (a0 * mu * mu2 + a1 * mu2 + a2 * mu + a3);
    }

    float mTargetMs{15.0f};
    float mIntegral{0.0f};
    float mCurrentRatio{1.0f};
    double mPhase{0.0};
    float mHistory[8]{0.0f}; // 4 past stereo frames (L,R)
};

} // namespace audiobridge
