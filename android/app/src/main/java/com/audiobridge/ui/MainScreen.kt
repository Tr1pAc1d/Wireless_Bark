package com.audiobridge.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.platform.LocalContext
import android.content.Context
import kotlin.math.roundToInt
import com.audiobridge.DiscoveredPc
import com.audiobridge.EngineStats

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainScreen(
    isStreaming: Boolean,
    connectedHost: String?,
    stats: EngineStats,
    discoveredPcs: List<DiscoveredPc>,
    onConnect: (host: String, port: Int, isUsb: Boolean, targetMs: Float) -> Unit,
    onDisconnect: () -> Unit,
    onTargetMsChanged: (Float) -> Unit
) {
    val context = LocalContext.current
    val prefs = remember { context.getSharedPreferences("audiobridge_settings", Context.MODE_PRIVATE) }
    var isUsbMode by remember { mutableStateOf(false) }
    var manualIp by remember { mutableStateOf("") }
    var targetMs by remember { mutableStateOf(prefs.getFloat("target_ms", 10f)) }

    fun setAndPersistTargetMs(newMs: Float) {
        val clamped = (newMs.roundToInt()).toFloat().coerceIn(3f, 60f)
        targetMs = clamped
        prefs.edit().putFloat("target_ms", clamped).apply()
        onTargetMsChanged(clamped)
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Wireless Bark", fontWeight = FontWeight.Bold) },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.primaryContainer,
                    titleContentColor = MaterialTheme.colorScheme.onPrimaryContainer
                )
            )
        }
    ) { innerPadding ->
        Column(
            modifier = Modifier
                .padding(innerPadding)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            // Mode Selector
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(16.dp)) {
                    Text("Connection Mode", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                    Spacer(modifier = Modifier.height(8.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        FilterChip(
                            selected = !isUsbMode,
                            onClick = { if (!isStreaming) isUsbMode = false },
                            label = { Text("Wi-Fi (UDP)") },
                            enabled = !isStreaming
                        )
                        FilterChip(
                            selected = isUsbMode,
                            onClick = { if (!isStreaming) isUsbMode = true },
                            label = { Text("USB (ADB Tunnel)") },
                            enabled = !isStreaming
                        )
                    }
                }
            }

            if (!isStreaming) {
                // Connection Configuration
                if (isUsbMode) {
                    Card(modifier = Modifier.fillMaxWidth()) {
                        Column(modifier = Modifier.padding(16.dp)) {
                            Text("USB Low-Latency Mode", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                            Spacer(modifier = Modifier.height(4.dp))
                            Text(
                                "1. Connect phone with USB cable.\n" +
                                "2. Ensure USB Debugging is ON in Developer Options.\n" +
                                "3. PC AudioBridge tray app tunnels port 47802 automatically.",
                                fontSize = 13.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Spacer(modifier = Modifier.height(16.dp))
                            Button(
                                onClick = { onConnect("127.0.0.1", 47802, true, targetMs) },
                                modifier = Modifier.fillMaxWidth()
                            ) {
                                Text("Connect over USB")
                            }
                        }
                    }
                } else {
                    Card(modifier = Modifier.fillMaxWidth()) {
                        Column(modifier = Modifier.padding(16.dp)) {
                            Text("Discovered PCs on Wi-Fi", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                            Spacer(modifier = Modifier.height(8.dp))
                            if (discoveredPcs.isEmpty()) {
                                Text(
                                    "Searching local network for PC... (Ensure PC tray app is running)",
                                    fontSize = 13.sp,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            } else {
                                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                    discoveredPcs.forEach { pc ->
                                        Row(
                                            modifier = Modifier
                                                .fillMaxWidth()
                                                .clip(RoundedCornerShape(8.dp))
                                                .background(MaterialTheme.colorScheme.surfaceVariant)
                                                .clickable { onConnect(pc.host, pc.udpPort, false, targetMs) }
                                                .padding(12.dp),
                                            horizontalArrangement = Arrangement.SpaceBetween,
                                            verticalAlignment = Alignment.CenterVertically
                                        ) {
                                            Column {
                                                Text(pc.name, fontWeight = FontWeight.Bold, fontSize = 15.sp)
                                                Text("${pc.host}:${pc.udpPort} (${pc.sampleRate} Hz)", fontSize = 12.sp)
                                            }
                                            FilledTonalButton(
                                                onClick = { onConnect(pc.host, pc.udpPort, false, targetMs) }
                                            ) {
                                                Text("Connect")
                                            }
                                        }
                                    }
                                }
                            }

                            Spacer(modifier = Modifier.height(16.dp))
                            Divider()
                            Spacer(modifier = Modifier.height(16.dp))

                            Text("Or connect manually by IP:", fontSize = 14.sp)
                            Spacer(modifier = Modifier.height(8.dp))
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                OutlinedTextField(
                                    value = manualIp,
                                    onValueChange = { manualIp = it },
                                    label = { Text("PC IP Address") },
                                    placeholder = { Text("192.168.1.100") },
                                    modifier = Modifier.weight(1f),
                                    singleLine = true
                                )
                                Button(
                                    onClick = {
                                        if (manualIp.isNotBlank()) {
                                            onConnect(manualIp.trim(), 47800, false, targetMs)
                                        }
                                    },
                                    enabled = manualIp.isNotBlank()
                                ) {
                                    Text("Connect")
                                }
                            }
                        }
                    }
                }
            } else {
                // Streaming Active Card
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer)
                ) {
                    Column(modifier = Modifier.padding(16.dp)) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Column {
                                Text("STREAMING ACTIVE", fontWeight = FontWeight.Bold, fontSize = 13.sp, color = MaterialTheme.colorScheme.primary)
                                Text(
                                    if (isUsbMode) "Connected via USB" else "Connected to $connectedHost",
                                    fontSize = 17.sp,
                                    fontWeight = FontWeight.SemiBold
                                )
                            }
                            Button(
                                onClick = onDisconnect,
                                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)
                            ) {
                                Text("Disconnect")
                            }
                        }
                    }
                }
            }

            // Latency Tuning Slider Card
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(16.dp)) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("Latency & Lip-Sync Buffer", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                        Surface(
                            shape = RoundedCornerShape(8.dp),
                            color = MaterialTheme.colorScheme.primaryContainer
                        ) {
                            Text(
                                "${targetMs.toInt()} ms",
                                fontWeight = FontWeight.Bold,
                                color = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                                fontSize = 14.sp
                            )
                        }
                    }

                    Spacer(modifier = Modifier.height(6.dp))
                    Text(
                        "For watching videos (YouTube, Movies), use 5ms or 10ms for instant lip-sync. Increase if you hear pops or crackles on noisy Wi-Fi.",
                        fontSize = 12.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )

                    Spacer(modifier = Modifier.height(10.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(6.dp)
                    ) {
                        listOf(
                            5f to "⚡ 5ms (Lipsync)",
                            10f to "🎯 10ms (Fast)",
                            15f to "⚖️ 15ms (Default)",
                            25f to "🛡️ 25ms (Stable)"
                        ).forEach { (preset, label) ->
                            FilterChip(
                                selected = (targetMs.toInt() == preset.toInt()),
                                onClick = { setAndPersistTargetMs(preset) },
                                label = { Text(label, fontSize = 11.sp) }
                            )
                        }
                    }

                    Spacer(modifier = Modifier.height(4.dp))
                    Slider(
                        value = targetMs,
                        onValueChange = { setAndPersistTargetMs(it) },
                        valueRange = 3f..60f
                    )
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Text("Ultra-Low / Zero Delay (3ms)", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Text("Stable (60ms)", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }

            // Live Performance & Latency Telemetry Card
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(16.dp)) {
                    Text("Live Audio Telemetry", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                    Spacer(modifier = Modifier.height(12.dp))

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        MetricItem("Est. Total Latency", "${stats.totalEstLatencyMs.toInt()} ms", isHighlight = true)
                        MetricItem("Jitter Buffer", String.format("%.1f ms", stats.bufferMs))
                        MetricItem("Hardware Out", String.format("%.1f ms", stats.outputLatencyMs))
                    }

                    Spacer(modifier = Modifier.height(12.dp))
                    Divider()
                    Spacer(modifier = Modifier.height(12.dp))

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        MetricItem("Network Jitter", String.format("%.2f ms", stats.jitterMs))
                        MetricItem("Clock Drift", String.format("%+.0f ppm", stats.driftPpm))
                        MetricItem("Sample Rate", "${stats.sampleRate} Hz")
                    }

                    Spacer(modifier = Modifier.height(12.dp))
                    Divider()
                    Spacer(modifier = Modifier.height(12.dp))

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        MetricItem("Packets Lost", "${stats.packetsLost}", isWarn = stats.packetsLost > 0)
                        MetricItem("Packets Late", "${stats.packetsLate}")
                        MetricItem("Underruns", "${stats.underruns}", isWarn = stats.underruns > 0)
                        MetricItem("Packets Rx", "${stats.packetsReceived}")
                    }
                }
            }
        }
    }
}

@Composable
private fun MetricItem(label: String, value: String, isHighlight: Boolean = false, isWarn: Boolean = false) {
    Column {
        Text(label, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(
            value,
            fontSize = if (isHighlight) 20.sp else 15.sp,
            fontWeight = if (isHighlight) FontWeight.ExtraBold else FontWeight.SemiBold,
            color = when {
                isHighlight -> MaterialTheme.colorScheme.primary
                isWarn -> MaterialTheme.colorScheme.error
                else -> Color.Unspecified
            }
        )
    }
}
