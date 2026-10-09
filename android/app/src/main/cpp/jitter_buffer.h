#pragma once

#include <vector>
#include <atomic>
#include <cmath>
#include <cstring>
#include <algorithm>

namespace audiobridge {

class JitterBuffer {
public:
    static constexpr size_t CAPACITY_FRAMES = 19200; // 400ms at 48kHz stereo

    JitterBuffer() : mBuffer(CAPACITY_FRAMES * 2, 0.0f) {
        reset();
    }

    void reset() {
        std::fill(mBuffer.begin(), mBuffer.end(), 0.0f);
        mReadFrameIndex.store(0, std::memory_order_relaxed);
        mHighestWrittenFrame.store(0, std::memory_order_relaxed);
        mInitialized.store(false, std::memory_order_release);
        mPrebuffering.store(true, std::memory_order_relaxed);
        mUnderruns.store(0, std::memory_order_relaxed);
        mLatePackets.store(0, std::memory_order_relaxed);
        mConsecutiveUnderruns = 0;
    }

    void setTargetMs(float ms) {
        mTargetMs.store(std::max(5.0f, std::min(100.0f, ms)), std::memory_order_relaxed);
    }

    void pushPacket(uint64_t sampleIndex, const int16_t* pcm, size_t frames, uint32_t sampleRate) {
        if (frames == 0) return;

        if (sampleRate > 0) {
            mSampleRate = sampleRate;
        }

        if (!mInitialized.load(std::memory_order_acquire)) {
            mReadFrameIndex.store(sampleIndex, std::memory_order_relaxed);
            mHighestWrittenFrame.store(sampleIndex + frames, std::memory_order_relaxed);
            mPrebuffering.store(true, std::memory_order_relaxed);
            mInitialized.store(true, std::memory_order_release);
        }

        uint64_t readIdx = mReadFrameIndex.load(std::memory_order_relaxed);
        if (sampleIndex + frames <= readIdx) {
            mLatePackets.fetch_add(1, std::memory_order_relaxed);
            return;
        }

        // Write frames into circular buffer
        for (size_t f = 0; f < frames; ++f) {
            uint64_t frameIdx = sampleIndex + f;
            size_t ringPos = (frameIdx % CAPACITY_FRAMES) * 2;
            mBuffer[ringPos] = pcm[f * 2] / 32768.0f;
            mBuffer[ringPos + 1] = pcm[f * 2 + 1] / 32768.0f;
        }

        uint64_t curHighest = mHighestWrittenFrame.load(std::memory_order_relaxed);
        if (sampleIndex + frames > curHighest) {
            mHighestWrittenFrame.store(sampleIndex + frames, std::memory_order_release);
        }

        // Check if prebuffering threshold has been reached
        if (mPrebuffering.load(std::memory_order_relaxed)) {
            uint64_t writeHead = sampleIndex + frames;
            readIdx = mReadFrameIndex.load(std::memory_order_relaxed);
            uint64_t buffered = (writeHead > readIdx) ? (writeHead - readIdx) : 0;
            uint64_t targetFrames = static_cast<uint64_t>((mSampleRate * mTargetMs.load(std::memory_order_relaxed)) / 1000.0f);
            if (targetFrames < 240) targetFrames = 240;
            if (buffered >= targetFrames) {
                mPrebuffering.store(false, std::memory_order_release);
            }
        }
    }

    // Called strictly by the audio rendering callback thread
    void pullFrames(float* outStereo, size_t count) {
        if (!mInitialized.load(std::memory_order_acquire) || mPrebuffering.load(std::memory_order_acquire)) {
            std::memset(outStereo, 0, count * 2 * sizeof(float));
            return;
        }

        uint64_t readHead = mReadFrameIndex.load(std::memory_order_relaxed);
        uint64_t writeHead = mHighestWrittenFrame.load(std::memory_order_acquire);

        for (size_t i = 0; i < count; ++i) {
            if (readHead < writeHead) {
                size_t ringPos = (readHead % CAPACITY_FRAMES) * 2;
                outStereo[i * 2] = mBuffer[ringPos];
                outStereo[i * 2 + 1] = mBuffer[ringPos + 1];

                mBuffer[ringPos] = 0.0f;
                mBuffer[ringPos + 1] = 0.0f;
                readHead++;
                mConsecutiveUnderruns = 0;
            } else {
                outStereo[i * 2] = 0.0f;
                outStereo[i * 2 + 1] = 0.0f;
                mUnderruns.fetch_add(1, std::memory_order_relaxed);
                mConsecutiveUnderruns++;
                if (mConsecutiveUnderruns >= 2400) {
                    mPrebuffering.store(true, std::memory_order_release);
                    mReadFrameIndex.store(writeHead, std::memory_order_relaxed);
                    mConsecutiveUnderruns = 0;
                    break;
                }
            }
        }

        mReadFrameIndex.store(readHead, std::memory_order_relaxed);
    }

    float getBufferFillMs(uint32_t sampleRate) const {
        if (!mInitialized.load(std::memory_order_relaxed) || sampleRate == 0) return 0.0f;
        uint64_t read = mReadFrameIndex.load(std::memory_order_relaxed);
        uint64_t write = mHighestWrittenFrame.load(std::memory_order_relaxed);
        if (write <= read) return 0.0f;
        int64_t diff = static_cast<int64_t>(write - read);
        return static_cast<float>(diff * 1000.0 / sampleRate);
    }

    bool isPrebuffering() const { return mPrebuffering.load(std::memory_order_relaxed); }
    uint32_t getUnderruns() const { return mUnderruns.load(std::memory_order_relaxed); }
    uint32_t getLatePackets() const { return mLatePackets.load(std::memory_order_relaxed); }

private:
    std::vector<float> mBuffer;
    std::atomic<uint64_t> mReadFrameIndex{0};
    std::atomic<uint64_t> mHighestWrittenFrame{0};
    std::atomic<bool> mInitialized{false};
    std::atomic<bool> mPrebuffering{true};
    std::atomic<uint32_t> mUnderruns{0};
    std::atomic<uint32_t> mLatePackets{0};
    std::atomic<float> mTargetMs{15.0f};
    uint32_t mSampleRate{48000};
    size_t mConsecutiveUnderruns{0};
};

} // namespace audiobridge
