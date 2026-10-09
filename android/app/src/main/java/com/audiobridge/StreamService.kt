package com.audiobridge

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

class StreamService : Service() {

    private val binder = LocalBinder()
    private val serviceScope = CoroutineScope(Dispatchers.Main + Job())
    private var statsJob: Job? = null

    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null

    private val _isStreaming = MutableStateFlow(false)
    val isStreaming: StateFlow<Boolean> = _isStreaming.asStateFlow()

    private val _stats = MutableStateFlow(EngineStats())
    val stats: StateFlow<EngineStats> = _stats.asStateFlow()

    private val _connectedHost = MutableStateFlow<String?>(null)
    val connectedHost: StateFlow<String?> = _connectedHost.asStateFlow()

    inner class LocalBinder : Binder() {
        val service: StreamService get() = this@StreamService
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onCreate() {
        super.onCreate()
        NativeEngine.initialize()
        createNotificationChannel()
    }

    fun startStreaming(host: String, port: Int, isUsb: Boolean, targetMs: Float): Boolean {
        acquireLocks(isUsb)
        val deviceName = "${Build.MANUFACTURER} ${Build.MODEL}"
        val ok = NativeEngine.start(host, port, isUsb, deviceName, targetMs)
        if (ok) {
            _isStreaming.value = true
            _connectedHost.value = host
            val notif = buildNotification(if (isUsb) "Streaming over USB" else "Streaming over Wi-Fi ($host)")
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(NOTIFICATION_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK)
            } else {
                startForeground(NOTIFICATION_ID, notif)
            }
            startStatsPolling()
        } else {
            releaseLocks()
        }
        return ok
    }

    fun stopStreaming() {
        statsJob?.cancel()
        NativeEngine.stop()
        releaseLocks()
        _isStreaming.value = false
        _connectedHost.value = null
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    fun updateTargetMs(targetMs: Float) {
        NativeEngine.setTargetMs(targetMs)
    }

    private fun startStatsPolling() {
        statsJob?.cancel()
        statsJob = serviceScope.launch {
            while (_isStreaming.value) {
                _stats.value = NativeEngine.getStats()
                delay(250)
            }
        }
    }

    private fun acquireLocks(isUsb: Boolean) {
        val pm = getSystemService(Context.POWER_SERVICE) as? PowerManager
        wakeLock = pm?.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AudioBridge:WakeLock")?.apply {
            acquire(12 * 60 * 60 * 1000L) // 12 hours max
        }

        if (!isUsb) {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            if (wm != null) {
                wifiLock = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    wm.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "AudioBridge:LowLatencyWifi")
                } else {
                    @Suppress("DEPRECATION")
                    wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "AudioBridge:HighPerfWifi")
                }.apply {
                    setReferenceCounted(false)
                    acquire()
                }
            }
        }
    }

    private fun releaseLocks() {
        try {
            if (wakeLock?.isHeld == true) wakeLock?.release()
        } catch (_: Exception) {}
        wakeLock = null

        try {
            if (wifiLock?.isHeld == true) wifiLock?.release()
        } catch (_: Exception) {}
        wifiLock = null
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "AudioBridge Playback",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Shows status while audio streaming is active"
            }
            val nm = getSystemService(NotificationManager::class.java)
            nm?.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(status: String): Notification {
        val launchIntent = Intent(this, MainActivity::class.java)
        val pi = PendingIntent.getActivity(
            this, 0, launchIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Wireless Bark Active")
            .setContentText(status)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentIntent(pi)
            .setOngoing(true)
            .build()
    }

    override fun onDestroy() {
        stopStreaming()
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL_ID = "audiobridge_stream"
        private const val NOTIFICATION_ID = 1001
    }
}
