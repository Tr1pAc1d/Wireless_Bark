#pragma once

#include "protocol.h"
#include "jitter_buffer.h"
#include <string>
#include <thread>
#include <atomic>
#include <functional>

namespace audiobridge {

enum class NetworkMode {
    WiFi_UDP = 0,
    USB_TCP = 1
};

struct ReceiverStats {
    float bufferMs;
    float targetMs;
    float outputLatencyMs;
    float jitterMs;
    float driftPpm;
    uint32_t packetsLost;
    uint32_t packetsLate;
    uint32_t underruns;
    uint32_t packetsReceived;
    uint32_t sampleRate;
    uint8_t channels;
};

class Receiver {
public:
    using FormatCallback = std::function<void(uint32_t sampleRate, uint8_t channels, uint16_t framesPerPacket)>;

    Receiver(JitterBuffer& jitter);
    ~Receiver();

    bool start(const std::string& host, int port, NetworkMode mode, const std::string& deviceName, FormatCallback onFormat);
    void stop();

    void setTargetMs(float ms) { mTargetMs.store(ms, std::memory_order_relaxed); }
    void setOutputLatencyMs(float ms) { mOutputLatencyMs.store(ms, std::memory_order_relaxed); }
    void setDriftPpm(float ppm) { mDriftPpm.store(ppm, std::memory_order_relaxed); }

    ReceiverStats getStats() const;

private:
    void udpLoop();
    void tcpLoop();
    void keepAliveLoop();

    JitterBuffer& mJitter;
    std::string mHost;
    int mPort{47800};
    NetworkMode mMode{NetworkMode::WiFi_UDP};
    std::string mDeviceName{"Android"};
    FormatCallback mOnFormat;

    std::atomic<bool> mRunning{false};
    std::thread mRxThread;
    std::thread mKaThread;

    int mSocketFd{-1};

    // Metrics
    std::atomic<float> mTargetMs{15.0f};
    std::atomic<float> mOutputLatencyMs{0.0f};
    std::atomic<float> mDriftPpm{0.0f};
    std::atomic<float> mAvgJitterMs{0.0f};
    std::atomic<uint32_t> mPacketsReceived{0};
    std::atomic<uint32_t> mPacketsLost{0};
    std::atomic<uint32_t> mSampleRate{44100};
    std::atomic<uint8_t> mChannels{2};

    uint32_t mLastSeq{0};
    bool mHasSeq{false};
};

} // namespace audiobridge
