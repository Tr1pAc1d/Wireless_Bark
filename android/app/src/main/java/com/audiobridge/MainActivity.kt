package com.audiobridge

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.*
import androidx.core.content.ContextCompat
import com.audiobridge.ui.MainScreen

class MainActivity : ComponentActivity() {

    private var streamService: StreamService? = null
    private var isBound = false
    private lateinit var discoveryListener: DiscoveryListener
    private val discoveredPcsState = mutableStateListOf<DiscoveredPc>()

    private val serviceConnection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, service: IBinder?) {
            val binder = service as? StreamService.LocalBinder
            streamService = binder?.service
            isBound = true
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            streamService = null
            isBound = false
        }
    }

    private val requestPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { /* Notification permission response */ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Request notification permission on Android 13+
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED) {
                requestPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }

        discoveryListener = DiscoveryListener(this)

        val serviceIntent = Intent(this, StreamService::class.java)
        bindService(serviceIntent, serviceConnection, Context.BIND_AUTO_CREATE)

        setContent {
            MaterialTheme {
                Surface {
                    val service = streamService
                    val isStreaming by service?.isStreaming?.collectAsState() ?: remember { mutableStateOf(false) }
                    val connectedHost by service?.connectedHost?.collectAsState() ?: remember { mutableStateOf(null) }
                    val stats by service?.stats?.collectAsState() ?: remember { mutableStateOf(EngineStats()) }

                    MainScreen(
                        isStreaming = isStreaming,
                        connectedHost = connectedHost,
                        stats = stats,
                        discoveredPcs = discoveredPcsState,
                        onConnect = { host, port, isUsb, targetMs ->
                            service?.startStreaming(host, port, isUsb, targetMs)
                        },
                        onDisconnect = {
                            service?.stopStreaming()
                        },
                        onTargetMsChanged = { targetMs ->
                            service?.updateTargetMs(targetMs)
                        }
                    )
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        discoveryListener.start { pcs ->
            discoveredPcsState.clear()
            discoveredPcsState.addAll(pcs)
        }
    }

    override fun onStop() {
        discoveryListener.stop()
        super.onStop()
    }

    override fun onDestroy() {
        if (isBound) {
            unbindService(serviceConnection)
            isBound = false
        }
        super.onDestroy()
    }
}
