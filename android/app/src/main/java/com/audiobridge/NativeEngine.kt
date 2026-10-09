package com.audiobridge

data class EngineStats(
    val bufferMs: Float = 0f,
    val targetMs: Float = 0f,
    val outputLatencyMs: Float = 0f,
    val jitterMs: Float = 0f,
    val driftPpm: Float = 0f,
    val packetsLost: Int = 0,
    val packetsLate: Int = 0,
    val underruns: Int = 0,
    val packetsReceived: Int = 0,
    val sampleRate: Int = 48000,
    val channels: Int = 2
) {
    val totalEstLatencyMs: Float
        get() = bufferMs + outputLatencyMs
}

object NativeEngine {
    init {
        System.loadLibrary("audiobridge_native")
    }

    private external fun nativeInit()
    private external fun nativeStart(
        host: String,
        port: Int,
        mode: Int,
        deviceName: String,
        targetMs: Float
    ): Boolean
    private external fun nativeStop()
    private external fun nativeSetTargetMs(targetMs: Float)
    private external fun nativeGetStats(floatData: FloatArray, intData: IntArray)
    external fun nativeSelfTest(): Boolean

    private val floatBuf = FloatArray(5)
    private val intBuf = IntArray(6)

    fun initialize() {
        nativeInit()
    }

    fun start(host: String, port: Int, isUsb: Boolean, deviceName: String, targetMs: Float): Boolean {
        val mode = if (isUsb) 1 else 0
        return nativeStart(host, port, mode, deviceName, targetMs)
    }

    fun stop() {
        nativeStop()
    }

    fun setTargetMs(targetMs: Float) {
        nativeSetTargetMs(targetMs)
    }

    @Synchronized
    fun getStats(): EngineStats {
        nativeGetStats(floatBuf, intBuf)
        return EngineStats(
            bufferMs = floatBuf[0],
            targetMs = floatBuf[1],
            outputLatencyMs = floatBuf[2],
            jitterMs = floatBuf[3],
            driftPpm = floatBuf[4],
            packetsLost = intBuf[0],
            packetsLate = intBuf[1],
            underruns = intBuf[2],
            packetsReceived = intBuf[3],
            sampleRate = intBuf[4],
            channels = intBuf[5]
        )
    }
}
