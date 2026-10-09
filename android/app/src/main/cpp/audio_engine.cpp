#include "audio_engine.h"
#include <android/log.h>

#define LOG_TAG "AudioBridgeNative"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace audiobridge {

AudioEngine::AudioEngine() {
    mReceiver = std::make_unique<Receiver>(mJitter);
}

AudioEngine::~AudioEngine() {
    stop();
}

bool AudioEngine::start(const std::string& host, int port, NetworkMode mode, const std::string& deviceName, float targetMs) {
    stop();
    mHost = host;
    mPort = port;
    mMode = mode;
    mDeviceName = deviceName;
    setTargetMs(targetMs);

    mJitter.reset();
    mResampler.reset();

    // Open default 48kHz stream first
    openStream(48000);

    // Start receiver
    mReceiver->start(mHost, mPort, mMode, mDeviceName, [this](uint32_t sampleRate, uint8_t /*channels*/, uint16_t /*fpp*/) {
        LOGI("Received FORMAT callback from PC: rate=%u", sampleRate);
        if (mSampleRate.load() != sampleRate || !mStreamOpen.load()) {
            openStream(sampleRate);
        }
    });

    return true;
}

void AudioEngine::stop() {
    if (mReceiver) mReceiver->stop();
    closeStream();
}

void AudioEngine::setTargetMs(float ms) {
    mTargetMs.store(ms, std::memory_order_relaxed);
    mResampler.setTargetMs(ms);
    if (mReceiver) mReceiver->setTargetMs(ms);
}

ReceiverStats AudioEngine::getStats() {
    ReceiverStats st{};
    if (mReceiver) {
        st = mReceiver->getStats();
    }
    {
        std::unique_lock<std::mutex> lock(mStreamMutex, std::try_to_lock);
        if (lock.owns_lock() && mStream && mStreamOpen.load()) {
            auto result = mStream->calculateLatencyMillis();
            if (result) {
                float lat = static_cast<float>(result.value());
                st.outputLatencyMs = lat;
                if (mReceiver) mReceiver->setOutputLatencyMs(lat);
            }
        }
    }
    st.driftPpm = mResampler.getDriftPpm();
    if (mReceiver) mReceiver->setDriftPpm(st.driftPpm);
    return st;
}

bool AudioEngine::openStream(uint32_t sampleRate) {
    std::lock_guard<std::mutex> lock(mStreamMutex);

    if (mStream && mStreamOpen.load() && mSampleRate.load() == sampleRate) {
        LOGI("Audio stream already open at %u Hz, skipping reopen", sampleRate);
        return true;
    }

    closeStreamLocked();

    mSampleRate.store(sampleRate);
    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Output)
           ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(oboe::SharingMode::Exclusive)
           ->setFormat(oboe::AudioFormat::Float)
           ->setChannelCount(oboe::ChannelCount::Stereo)
           ->setSampleRate(static_cast<int32_t>(sampleRate))
           ->setDataCallback(this)
           ->setErrorCallback(this);

    oboe::Result result = builder.openStream(mStream);
    if (result != oboe::Result::OK) {
        LOGE("Failed to open exclusive stream (%s), trying Shared mode...", oboe::convertToText(result));
        builder.setSharingMode(oboe::SharingMode::Shared);
        result = builder.openStream(mStream);
        if (result != oboe::Result::OK) {
            LOGE("Failed to open shared stream: %s", oboe::convertToText(result));
            return false;
        }
    }

    // Set buffer size to burst size (2 bursts is standard for glitch-free low latency)
    int32_t burst = mStream->getFramesPerBurst();
    if (burst > 0) {
        mStream->setBufferSizeInFrames(burst * 2);
    }

    result = mStream->requestStart();
    if (result != oboe::Result::OK) {
        LOGE("Failed to start audio stream: %s", oboe::convertToText(result));
        mStream->close();
        mStream.reset();
        return false;
    }

    mStreamOpen.store(true);
    LOGI("Audio stream started successfully at %u Hz, burst=%d, buffer=%d frames",
         sampleRate, burst, mStream->getBufferSizeInFrames());
    return true;
}

void AudioEngine::closeStream() {
    std::lock_guard<std::mutex> lock(mStreamMutex);
    closeStreamLocked();
}

void AudioEngine::closeStreamLocked() {
    mStreamOpen.store(false);
    if (mStream) {
        try {
            mStream->stop();
            mStream->close();
        } catch (...) {}
        mStream.reset();
    }
}

oboe::DataCallbackResult AudioEngine::onAudioReady(oboe::AudioStream* /*oboeStream*/, void* audioData, int32_t numFrames) {
    float* dst = static_cast<float*>(audioData);
    mResampler.render(mJitter, dst, numFrames, mSampleRate.load(std::memory_order_relaxed));
    return oboe::DataCallbackResult::Continue;
}

void AudioEngine::onErrorAfterClose(oboe::AudioStream* /*oboeStream*/, oboe::Result error) {
    LOGI("AudioStream onErrorAfterClose: %s. Reopening...", oboe::convertToText(error));
    openStream(mSampleRate.load());
}

} // namespace audiobridge
