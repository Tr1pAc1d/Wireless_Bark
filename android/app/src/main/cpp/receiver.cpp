#include "receiver.h"
#include <sys/socket.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <fcntl.h>
#include <android/log.h>
#include <chrono>
#include <cmath>

#define LOG_TAG "AudioBridgeNative"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace audiobridge {

Receiver::Receiver(JitterBuffer& jitter) : mJitter(jitter) {}

Receiver::~Receiver() {
    stop();
}

bool Receiver::start(const std::string& host, int port, NetworkMode mode, const std::string& deviceName, FormatCallback onFormat) {
    stop();
    mHost = host;
    mPort = port;
    mMode = mode;
    mDeviceName = deviceName;
    mOnFormat = onFormat;

    mRunning.store(true, std::memory_order_release);
    mPacketsReceived.store(0, std::memory_order_relaxed);
    mPacketsLost.store(0, std::memory_order_relaxed);
    mHasSeq = false;

    if (mMode == NetworkMode::WiFi_UDP) {
        mRxThread = std::thread(&Receiver::udpLoop, this);
    } else {
        mRxThread = std::thread(&Receiver::tcpLoop, this);
    }
    mKaThread = std::thread(&Receiver::keepAliveLoop, this);
    return true;
}

void Receiver::stop() {
    if (!mRunning.exchange(false, std::memory_order_acq_rel)) return;

    if (mSocketFd >= 0) {
        shutdown(mSocketFd, SHUT_RDWR);
        close(mSocketFd);
        mSocketFd = -1;
    }

    if (mRxThread.joinable()) mRxThread.join();
    if (mKaThread.joinable()) mKaThread.join();
}

ReceiverStats Receiver::getStats() const {
    ReceiverStats s;
    s.bufferMs = mJitter.getBufferFillMs(mSampleRate.load(std::memory_order_relaxed));
    s.targetMs = mTargetMs.load(std::memory_order_relaxed);
    s.outputLatencyMs = mOutputLatencyMs.load(std::memory_order_relaxed);
    s.jitterMs = mAvgJitterMs.load(std::memory_order_relaxed);
    s.driftPpm = mDriftPpm.load(std::memory_order_relaxed);
    s.packetsLost = mPacketsLost.load(std::memory_order_relaxed);
    s.packetsLate = mJitter.getLatePackets();
    s.underruns = mJitter.getUnderruns();
    s.packetsReceived = mPacketsReceived.load(std::memory_order_relaxed);
    s.sampleRate = mSampleRate.load(std::memory_order_relaxed);
    s.channels = mChannels.load(std::memory_order_relaxed);
    return s;
}

void Receiver::udpLoop() {
    mSocketFd = socket(AF_INET, SOCK_DGRAM, 0);
    if (mSocketFd < 0) {
        LOGE("Failed to create UDP socket");
        return;
    }

    // Set high receive buffer
    int rcvbuf = 256 * 1024;
    setsockopt(mSocketFd, SOL_SOCKET, SO_RCVBUF, &rcvbuf, sizeof(rcvbuf));

    // Bind to 0.0.0.0:0
    sockaddr_in localAddr{};
    localAddr.sin_family = AF_INET;
    localAddr.sin_addr.s_addr = INADDR_ANY;
    localAddr.sin_port = 0;
    bind(mSocketFd, reinterpret_cast<sockaddr*>(&localAddr), sizeof(localAddr));

    sockaddr_in serverAddr{};
    serverAddr.sin_family = AF_INET;
    serverAddr.sin_port = htons(mPort);
    inet_pton(AF_INET, mHost.c_str(), &serverAddr.sin_addr);

    // Send HELLO
    uint8_t helloBuf[128];
    Header* h = reinterpret_cast<Header*>(helloBuf);
    h->magic = WIRE_MAGIC;
    h->type = MessageType::Hello;
    h->flags = 0;
    h->seq = 1;
    h->value = 0;

    uint8_t* p = helloBuf + WIRE_HEADER_SIZE;
    *reinterpret_cast<uint16_t*>(p) = 1; // protocolVersion
    *reinterpret_cast<uint16_t*>(p + 2) = 0;
    uint8_t nameLen = static_cast<uint8_t>(std::min(mDeviceName.size(), size_t(32)));
    p[4] = nameLen;
    std::memcpy(p + 5, mDeviceName.data(), nameLen);
    size_t helloSize = WIRE_HEADER_SIZE + 5 + nameLen;

    sendto(mSocketFd, helloBuf, helloSize, 0, reinterpret_cast<sockaddr*>(&serverAddr), sizeof(serverAddr));
    LOGI("Sent UDP HELLO to %s:%d", mHost.c_str(), mPort);

    uint8_t rxBuf[2048];
    auto lastArrival = std::chrono::steady_clock::now();
    double jitterAcc = 0;
    int jitterCount = 0;

    while (mRunning.load(std::memory_order_relaxed)) {
        sockaddr_in fromAddr{};
        socklen_t fromLen = sizeof(fromAddr);
        ssize_t n = recvfrom(mSocketFd, rxBuf, sizeof(rxBuf), 0, reinterpret_cast<sockaddr*>(&fromAddr), &fromLen);
        if (n < static_cast<ssize_t>(WIRE_HEADER_SIZE)) continue;

        auto now = std::chrono::steady_clock::now();
        double elapsedMs = std::chrono::duration<double, std::milli>(now - lastArrival).count();
        lastArrival = now;
        double diff = std::abs(elapsedMs - 5.0); // 5ms packets
        jitterAcc += diff;
        jitterCount++;
        if (jitterCount >= 100) {
            mAvgJitterMs.store(static_cast<float>(jitterAcc / jitterCount), std::memory_order_relaxed);
            jitterAcc = 0;
            jitterCount = 0;
        }

        const Header* inHdr = reinterpret_cast<const Header*>(rxBuf);
        if (inHdr->magic != WIRE_MAGIC) continue;

        if (inHdr->type == MessageType::Audio) {
            mPacketsReceived.fetch_add(1, std::memory_order_relaxed);
            if (mHasSeq) {
                uint32_t gap = inHdr->seq - mLastSeq;
                if (gap > 1) mPacketsLost.fetch_add(gap - 1, std::memory_order_relaxed);
            }
            mLastSeq = inHdr->seq;
            mHasSeq = true;

            size_t pcmBytes = n - WIRE_HEADER_SIZE;
            uint8_t ch = std::max<uint8_t>(1, mChannels.load(std::memory_order_relaxed));
            size_t frames = pcmBytes / (ch * sizeof(int16_t));
            const int16_t* pcm = reinterpret_cast<const int16_t*>(rxBuf + WIRE_HEADER_SIZE);

            if (frames == 220 || frames == 441) {
                if (mSampleRate.load(std::memory_order_relaxed) != 44100) {
                    mSampleRate.store(44100, std::memory_order_relaxed);
                    if (mOnFormat) mOnFormat(44100, ch, static_cast<uint16_t>(frames));
                }
            } else if (frames == 240 || frames == 480) {
                if (mSampleRate.load(std::memory_order_relaxed) != 48000) {
                    mSampleRate.store(48000, std::memory_order_relaxed);
                    if (mOnFormat) mOnFormat(48000, ch, static_cast<uint16_t>(frames));
                }
            }

            mJitter.pushPacket(inHdr->value, pcm, frames, mSampleRate.load(std::memory_order_relaxed));
        } else if (inHdr->type == MessageType::Format) {
            if (n >= static_cast<ssize_t>(WIRE_HEADER_SIZE + sizeof(FormatPayload))) {
                const FormatPayload* fmt = reinterpret_cast<const FormatPayload*>(rxBuf + WIRE_HEADER_SIZE);
                if (fmt->sampleRate > 0) mSampleRate.store(fmt->sampleRate, std::memory_order_relaxed);
                if (fmt->channels > 0) mChannels.store(fmt->channels, std::memory_order_relaxed);
                if (mOnFormat && fmt->sampleRate > 0) mOnFormat(fmt->sampleRate, fmt->channels, fmt->framesPerPacket);
            }
        } else if (inHdr->type == MessageType::Ping) {
            Header pongHdr = *inHdr;
            pongHdr.type = MessageType::Pong;
            sendto(mSocketFd, &pongHdr, sizeof(pongHdr), 0, reinterpret_cast<sockaddr*>(&serverAddr), sizeof(serverAddr));
        }
    }
}

void Receiver::tcpLoop() {
    mSocketFd = socket(AF_INET, SOCK_STREAM, 0);
    if (mSocketFd < 0) {
        LOGE("Failed to create TCP socket");
        return;
    }

    int flag = 1;
    setsockopt(mSocketFd, IPPROTO_TCP, TCP_NODELAY, &flag, sizeof(flag));

    sockaddr_in serverAddr{};
    serverAddr.sin_family = AF_INET;
    serverAddr.sin_port = htons(mPort);
    inet_pton(AF_INET, mHost.c_str(), &serverAddr.sin_addr);

    if (connect(mSocketFd, reinterpret_cast<sockaddr*>(&serverAddr), sizeof(serverAddr)) < 0) {
        LOGE("Failed to connect to TCP server %s:%d", mHost.c_str(), mPort);
        return;
    }
    LOGI("Connected to TCP %s:%d", mHost.c_str(), mPort);

    // Send HELLO with length prefix
    uint8_t helloBuf[128];
    Header* h = reinterpret_cast<Header*>(helloBuf + 2);
    h->magic = WIRE_MAGIC;
    h->type = MessageType::Hello;
    h->flags = 0;
    h->seq = 1;
    h->value = 0;

    uint8_t* p = helloBuf + 2 + WIRE_HEADER_SIZE;
    *reinterpret_cast<uint16_t*>(p) = 1; // protocolVersion
    *reinterpret_cast<uint16_t*>(p + 2) = 0;
    uint8_t nameLen = static_cast<uint8_t>(std::min(mDeviceName.size(), size_t(32)));
    p[4] = nameLen;
    std::memcpy(p + 5, mDeviceName.data(), nameLen);
    uint16_t bodyLen = static_cast<uint16_t>(WIRE_HEADER_SIZE + 5 + nameLen);
    *reinterpret_cast<uint16_t*>(helloBuf) = bodyLen;

    send(mSocketFd, helloBuf, bodyLen + 2, 0);

    uint8_t rxBuf[2048];
    while (mRunning.load(std::memory_order_relaxed)) {
        uint16_t msgLen = 0;
        size_t readLen = 0;
        while (readLen < 2) {
            ssize_t r = recv(mSocketFd, reinterpret_cast<char*>(&msgLen) + readLen, 2 - readLen, 0);
            if (r <= 0) return;
            readLen += r;
        }

        if (msgLen < WIRE_HEADER_SIZE || msgLen > sizeof(rxBuf)) break;

        size_t bodyGot = 0;
        while (bodyGot < msgLen) {
            ssize_t r = recv(mSocketFd, rxBuf + bodyGot, msgLen - bodyGot, 0);
            if (r <= 0) return;
            bodyGot += r;
        }

        const Header* inHdr = reinterpret_cast<const Header*>(rxBuf);
        if (inHdr->magic != WIRE_MAGIC) continue;

        if (inHdr->type == MessageType::Audio) {
            mPacketsReceived.fetch_add(1, std::memory_order_relaxed);
            size_t pcmBytes = msgLen - WIRE_HEADER_SIZE;
            uint8_t ch = std::max<uint8_t>(1, mChannels.load(std::memory_order_relaxed));
            size_t frames = pcmBytes / (ch * sizeof(int16_t));
            const int16_t* pcm = reinterpret_cast<const int16_t*>(rxBuf + WIRE_HEADER_SIZE);

            if (frames == 220 || frames == 441) {
                if (mSampleRate.load(std::memory_order_relaxed) != 44100) {
                    mSampleRate.store(44100, std::memory_order_relaxed);
                    if (mOnFormat) mOnFormat(44100, ch, static_cast<uint16_t>(frames));
                }
            } else if (frames == 240 || frames == 480) {
                if (mSampleRate.load(std::memory_order_relaxed) != 48000) {
                    mSampleRate.store(48000, std::memory_order_relaxed);
                    if (mOnFormat) mOnFormat(48000, ch, static_cast<uint16_t>(frames));
                }
            }

            mJitter.pushPacket(inHdr->value, pcm, frames, mSampleRate.load(std::memory_order_relaxed));
        } else if (inHdr->type == MessageType::Format) {
            if (msgLen >= WIRE_HEADER_SIZE + sizeof(FormatPayload)) {
                const FormatPayload* fmt = reinterpret_cast<const FormatPayload*>(rxBuf + WIRE_HEADER_SIZE);
                if (fmt->sampleRate > 0) mSampleRate.store(fmt->sampleRate, std::memory_order_relaxed);
                if (fmt->channels > 0) mChannels.store(fmt->channels, std::memory_order_relaxed);
                if (mOnFormat && fmt->sampleRate > 0) mOnFormat(fmt->sampleRate, fmt->channels, fmt->framesPerPacket);
            }
        } else if (inHdr->type == MessageType::Ping) {
            uint8_t pongBuf[WIRE_HEADER_SIZE + 2];
            *reinterpret_cast<uint16_t*>(pongBuf) = WIRE_HEADER_SIZE;
            Header* pongHdr = reinterpret_cast<Header*>(pongBuf + 2);
            *pongHdr = *inHdr;
            pongHdr->type = MessageType::Pong;
            send(mSocketFd, pongBuf, WIRE_HEADER_SIZE + 2, 0);
        }
    }
}

void Receiver::keepAliveLoop() {
    uint8_t kaBuf[128];
    while (mRunning.load(std::memory_order_relaxed)) {
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
        if (!mRunning.load(std::memory_order_relaxed) || mSocketFd < 0) continue;

        ReceiverStats st = getStats();
        if (mMode == NetworkMode::WiFi_UDP) {
            Header* h = reinterpret_cast<Header*>(kaBuf);
            h->magic = WIRE_MAGIC;
            h->type = MessageType::KeepAlive;
            h->flags = 0;
            h->seq = 1;
            h->value = 0;

            KeepAlivePayload* payload = reinterpret_cast<KeepAlivePayload*>(kaBuf + WIRE_HEADER_SIZE);
            payload->bufferMs = st.bufferMs;
            payload->targetMs = st.targetMs;
            payload->outputLatencyMs = st.outputLatencyMs;
            payload->jitterMs = st.jitterMs;
            payload->driftPpm = st.driftPpm;
            payload->packetsLost = st.packetsLost;
            payload->packetsLate = st.packetsLate;
            payload->underruns = st.underruns;
            payload->packetsReceived = st.packetsReceived;

            sockaddr_in serverAddr{};
            serverAddr.sin_family = AF_INET;
            serverAddr.sin_port = htons(mPort);
            inet_pton(AF_INET, mHost.c_str(), &serverAddr.sin_addr);
            sendto(mSocketFd, kaBuf, WIRE_HEADER_SIZE + sizeof(KeepAlivePayload), 0,
                   reinterpret_cast<sockaddr*>(&serverAddr), sizeof(serverAddr));
        } else {
            *reinterpret_cast<uint16_t*>(kaBuf) = WIRE_HEADER_SIZE + sizeof(KeepAlivePayload);
            Header* h = reinterpret_cast<Header*>(kaBuf + 2);
            h->magic = WIRE_MAGIC;
            h->type = MessageType::KeepAlive;
            h->flags = 0;
            h->seq = 1;
            h->value = 0;

            KeepAlivePayload* payload = reinterpret_cast<KeepAlivePayload*>(kaBuf + 2 + WIRE_HEADER_SIZE);
            payload->bufferMs = st.bufferMs;
            payload->targetMs = st.targetMs;
            payload->outputLatencyMs = st.outputLatencyMs;
            payload->jitterMs = st.jitterMs;
            payload->driftPpm = st.driftPpm;
            payload->packetsLost = st.packetsLost;
            payload->packetsLate = st.packetsLate;
            payload->underruns = st.underruns;
            payload->packetsReceived = st.packetsReceived;

            send(mSocketFd, kaBuf, 2 + WIRE_HEADER_SIZE + sizeof(KeepAlivePayload), 0);
        }
    }
}

} // namespace audiobridge
