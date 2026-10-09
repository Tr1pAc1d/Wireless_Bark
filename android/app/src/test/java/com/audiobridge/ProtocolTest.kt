package com.audiobridge

import org.junit.Assert.assertEquals
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

class ProtocolTest {

    @Test
    fun testKeepAlivePayloadSizeAndLayout() {
        val bb = ByteBuffer.allocate(16 + 36).order(ByteOrder.LITTLE_ENDIAN)
        bb.putShort(0xAB01.toShort()) // magic
        bb.put(5) // type = KeepAlive
        bb.put(0) // flags
        bb.putInt(10) // seq
        bb.putLong(0) // value

        // Payload
        bb.putFloat(15.5f) // bufferMs
        bb.putFloat(15.0f) // targetMs
        bb.putFloat(10.2f) // outputLatencyMs
        bb.putFloat(0.8f)  // jitterMs
        bb.putFloat(-12f)  // driftPpm
        bb.putInt(0)       // packetsLost
        bb.putInt(1)       // packetsLate
        bb.putInt(0)       // underruns
        bb.putInt(500)     // packetsReceived

        bb.flip()
        assertEquals(52, bb.limit())
        assertEquals(0xAB01.toShort(), bb.short)
        assertEquals(5.toByte(), bb.get())
    }
}
