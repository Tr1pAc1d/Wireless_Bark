#pragma once

#include "jitter_buffer.h"
#include <vector>
#include <cmath>
#include <cstring>
#include <algorithm>

namespace audiobridge {

class DriftResampler {
public:
    DriftResampler() = default;

    void reset() {
        mIntegral = 0.0f;
        mBaseRatio = 1.0;
        mCurrentRatio = 1.0;
        mPhase = 0.0;
        std::fill(std::begin(mHistory), std::end(mHistory), 0.0f);
        mHistoryFilled = false;
    }

    void setTargetMs(float targetMs) {
        mTargetMs = std::max(2.0f, std::min(100.0f, targetMs));
    }

    float getDriftPpm() const {
        return static_cast<float>((mCurrentRatio - mBaseRatio) * 1e6 / (mBaseRatio > 0.0 ? mBaseRatio : 1.0));
    }

    // Called on audio thread in onAudioReady
    void render(JitterBuffer& jitter, float* outStereo, size_t outFrames, uint32_t inRate, uint32_t outRate) {
        if (inRate == 0) inRate = 44100;
        if (outRate == 0) outRate = 48000;

        jitter.setTargetMs(mTargetMs);

        // Compute base conversion ratio (e.g. 44100 / 48000 = 0.91875)
        mBaseRatio = static_cast<double>(inRate) / static_cast<double>(outRate);

        if (jitter.isPrebuffering()) {
            std::memset(outStereo, 0, outFrames * 2 * sizeof(float));
            mHistoryFilled = false;
            mPhase = 0.0;
            mIntegral = 0.0f;
            mCurrentRatio = mBaseRatio;
            return;
        }

        // PI controller update to fine-tune around baseRatio
        float currentFillMs = jitter.getBufferFillMs(inRate);
        float error = currentFillMs - mTargetMs; // positive if buffer has too much audio

        constexpr float Kp = 0.00005f;
        constexpr float Ki = 0.000001f;

        mIntegral += error;
        mIntegral = std::max(-500.0f, std::min(500.0f, mIntegral));

        float adjustment = (Kp * error) + (Ki * mIntegral);
        adjustment = std::max(-0.02f, std::min(0.02f, adjustment)); // clamp to +/- 2%
        mCurrentRatio = mBaseRatio * (1.0 + adjustment);

        // Direct pass-through fast path if rates are identical and no drift adjustment
        if (inRate == outRate && std::abs(mCurrentRatio - 1.0) < 0.0001 && mPhase == 0.0) {
            jitter.pullFrames(outStereo, outFrames);
            return;
        }

        // Initialize history with initial samples if needed
        if (!mHistoryFilled) {
            float initSample[2] = {0.0f, 0.0f};
            jitter.pullFrames(initSample, 1);
            for (int k = 0; k < 4; ++k) {
                mHistory[k * 2] = initSample[0];
                mHistory[k * 2 + 1] = initSample[1];
            }
            mHistoryFilled = true;
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

            // Catmull-Rom cubic spline interpolation
            float mu = static_cast<float>(mPhase);
            outStereo[i * 2] = interpolateCubic(mHistory[0], mHistory[2], mHistory[4], mHistory[6], mu);
            outStereo[i * 2 + 1] = interpolateCubic(mHistory[1], mHistory[3], mHistory[5], mHistory[7], mu);

            mPhase += mCurrentRatio;
        }
    }

private:
    static inline float interpolateCubic(float y0, float y1, float y2, float y3, float mu) {
        float mu2 = mu * mu;
        float a0 = -0.5f * y0 + 1.5f * y1 - 1.5f * y2 + 0.5f * y3;
        float a1 = y0 - 2.5f * y1 + 2.0f * y2 - 0.5f * y3;
        float a2 = -0.5f * y0 + 0.5f * y2;
        float a3 = y1;
        return (a0 * mu * mu2 + a1 * mu2 + a2 * mu + a3);
    }

    float mTargetMs{15.0f};
    float mIntegral{0.0f};
    double mBaseRatio{1.0};
    double mCurrentRatio{1.0};
    double mPhase{0.0};
    float mHistory[8]{0.0f};
    bool mHistoryFilled{false};
};

} // namespace audiobridge
