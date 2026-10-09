# AudioBridge Wire Protocol v1

All integers are **little-endian**. Floats are IEEE-754 32-bit little-endian.

## Transports & ports

| Port | Proto | Bound on | Purpose |
|---|---|---|---|
| 47800 | UDP | PC `0.0.0.0` | Control + audio (Wi-Fi mode). The phone uses **one** UDP socket for everything; the PC replies to the source address of `HELLO`. |
| 47801 | UDP | Phone | Discovery beacons (PC broadcasts, phone listens). |
| 47802 | TCP | PC `127.0.0.1` | USB mode. The phone connects to `127.0.0.1:47802`, which `adb reverse tcp:47802 tcp:47802` tunnels to the PC. |

In **TCP** mode every message is prefixed with a `u16` byte length (the length of the message that follows, header included). In **UDP** mode one datagram = one message.

## Common header (16 bytes)

| Offset | Type | Field |
|---|---|---|
| 0 | u16 | magic = `0xAB01` |
| 2 | u8 | type |
| 3 | u8 | flags (reserved, 0) |
| 4 | u32 | seq (per-sender, per-type counter; for AUDIO it increments by 1 per packet) |
| 8 | u64 | `value` - meaning depends on type (AUDIO: sampleIndex of first frame; PING/PONG: PC timestamp ticks) |

## Message types

| Type | Name | Direction | Payload after header |
|---|---|---|---|
| 1 | DISCOVERY | PC → broadcast :47801 | `u16 udpPort, u16 tcpPort, u32 sampleRate, u8 channels, u8 nameLen, u8[nameLen] name (UTF-8)` |
| 2 | HELLO | phone → PC | `u16 protocolVersion (=1), u16 reserved, u8 nameLen, u8[nameLen] deviceName (UTF-8)` |
| 3 | FORMAT | PC → phone | `u32 sampleRate, u8 channels (=2), u8 bitsPerSample (=16), u16 framesPerPacket` |
| 4 | AUDIO | PC → phone | `int16[framesPerPacket * channels]` interleaved PCM. `value` = sampleIndex (frames since stream start). |
| 5 | KEEPALIVE | phone → PC | `f32 bufferMs, f32 targetMs, f32 outputLatencyMs, f32 jitterMs, f32 driftPpm, u32 packetsLost, u32 packetsLate, u32 underruns, u32 packetsReceived` |
| 6 | PING | PC → phone | none. `value` = PC timestamp (Stopwatch ticks) |
| 7 | PONG | phone → PC | none. `value` echoed from PING |
| 8 | BYE | either | none |

## Session rules

1. Phone sends `HELLO` (UDP: to PC:47800; TCP: right after connecting). It re-sends `HELLO` every second until it receives `FORMAT`, and again if no AUDIO arrives for 2 s.
2. PC answers with `FORMAT`, then streams `AUDIO` every `framesPerPacket` frames (5 ms).
3. Phone sends `KEEPALIVE` every 500 ms. PC drops the client after 3 s of silence.
4. PC sends `PING` every second; phone answers `PONG` immediately → PC computes RTT.
5. When the capture device/format changes, the PC sends a new `FORMAT` and restarts `sampleIndex` at 0. The phone resets its jitter buffer whenever it gets `FORMAT` or sees a sampleIndex discontinuity.
