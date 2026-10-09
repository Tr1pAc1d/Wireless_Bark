#pragma once

#include <oboe/Oboe.h>
#include "jitter_buffer.h"
#include "drift_resampler.h"
#include "receiver.h"
#include <memory>
#include <atomic>

namespace audiobridge {

class AudioEngine : public oboe::AudioStreamDataCallback, public oboe::AudioStreamErrorCallback {
public:
    AudioEngine();
    ~AudioEngine();

    bool start(const std::string& host, int port, NetworkMode mode, const std::string& deviceName, float targetMs);
    void stop();

    void setTargetMs(float ms);
    ReceiverStats getStats();

    // oboe::AudioStreamDataCallback
    oboe::DataCallbackResult onAudioReady(oboe::AudioStream* oboeStream, void* audioData, int32_t numFrames) override;

    // oboe::AudioStreamErrorCallback
    void onErrorAfterClose(oboe::AudioStream* oboeStream, oboe::Result error) override;

private:
    bool openStream(uint32_t sampleRate);
    void closeStream();
    void closeStreamLocked();

    std::mutex mStreamMutex;
    JitterBuffer mJitter;
    DriftResampler mResampler;
    std::unique_ptr<Receiver> mReceiver;

    std::shared_ptr<oboe::AudioStream> mStream;
    std::atomic<uint32_t> mSampleRate{48000};
    std::atomic<bool> mStreamOpen{false};
    std::atomic<float> mTargetMs{15.0f};

    std::string mHost;
    int mPort{47800};
    NetworkMode mMode{NetworkMode::WiFi_UDP};
    std::string mDeviceName{"Android"};
};

} // namespace audiobridge
