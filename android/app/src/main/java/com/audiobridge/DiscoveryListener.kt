package com.audiobridge

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder

data class DiscoveredPc(
    val name: String,
    val host: String,
    val udpPort: Int,
    val tcpPort: Int,
    val sampleRate: Int,
    val channels: Int,
    val lastSeenTimeMs: Long
)

class DiscoveryListener(private val context: Context) {
    private var socket: DatagramSocket? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    private var isListening = false
    private val mainHandler = Handler(Looper.getMainLooper())

    private val discovered = mutableMapOf<String, DiscoveredPc>()

    fun start(onUpdate: (List<DiscoveredPc>) -> Unit) {
        if (isListening) return
        isListening = true

        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        multicastLock = wifi?.createMulticastLock("AudioBridgeDiscovery")?.apply {
            setReferenceCounted(true)
            acquire()
        }

        Thread {
            try {
                val s = DatagramSocket(null).apply {
                    reuseAddress = true
                    bind(InetSocketAddress(47801))
                    broadcast = true
                }
                socket = s

                val buf = ByteArray(1024)
                while (isListening) {
                    val p = DatagramPacket(buf, buf.size)
                    s.receive(p)

                    if (p.length < 16) continue
                    val bb = ByteBuffer.wrap(buf, 0, p.length).order(ByteOrder.LITTLE_ENDIAN)
                    val magic = bb.short.toInt() and 0xFFFF
                    if (magic != 0xAB01) continue

                    val type = bb.get().toInt()
                    if (type != 1) continue // Discovery message

                    bb.get() // flags
                    bb.int // seq
                    bb.long // value

                    if (bb.remaining() < 9) continue
                    val udpPort = bb.short.toInt() and 0xFFFF
                    val tcpPort = bb.short.toInt() and 0xFFFF
                    val rate = bb.int
                    val channels = bb.get().toInt() and 0xFF
                    val nameLen = bb.get().toInt() and 0xFF
                    val actualLen = minOf(nameLen, bb.remaining())
                    val nameBytes = ByteArray(actualLen)
                    bb.get(nameBytes)
                    val pcName = String(nameBytes, Charsets.UTF_8)

                    val senderIp = p.address.hostAddress ?: continue
                    val pc = DiscoveredPc(
                        name = pcName,
                        host = senderIp,
                        udpPort = udpPort,
                        tcpPort = tcpPort,
                        sampleRate = rate,
                        channels = channels,
                        lastSeenTimeMs = System.currentTimeMillis()
                    )

                    synchronized(discovered) {
                        discovered[senderIp] = pc
                        // Prune items not seen in 5s
                        val now = System.currentTimeMillis()
                        discovered.entries.removeIf { now - it.value.lastSeenTimeMs > 5000 }
                        val list = discovered.values.toList()
                        mainHandler.post { onUpdate(list) }
                    }
                }
            } catch (_: Exception) {
            } finally {
                socket?.close()
                socket = null
            }
        }.start()
    }

    fun stop() {
        isListening = false
        try { socket?.close() } catch (_: Exception) {}
        try { multicastLock?.release() } catch (_: Exception) {}
        multicastLock = null
    }
}
