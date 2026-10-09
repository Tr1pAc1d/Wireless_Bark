#pragma once

#include <cstdint>

namespace audiobridge {

#pragma pack(push, 1)

constexpr uint16_t WIRE_MAGIC = 0xAB01;
constexpr size_t WIRE_HEADER_SIZE = 16;
constexpr size_t MAX_MESSAGE_SIZE = 1400;

enum class MessageType : uint8_t {
    Discovery = 1,
    Hello = 2,
    Format = 3,
    Audio = 4,
    KeepAlive = 5,
    Ping = 6,
    Pong = 7,
    Bye = 8
};

struct Header {
    uint16_t magic;
    MessageType type;
    uint8_t flags;
    uint32_t seq;
    uint64_t value;
};

struct FormatPayload {
    uint32_t sampleRate;
    uint8_t channels;
    uint8_t bitsPerSample;
    uint16_t framesPerPacket;
};

struct KeepAlivePayload {
    float bufferMs;
    float targetMs;
    float outputLatencyMs;
    float jitterMs;
    float driftPpm;
    uint32_t packetsLost;
    uint32_t packetsLate;
    uint32_t underruns;
    uint32_t packetsReceived;
};

#pragma pack(pop)

} // namespace audiobridge
