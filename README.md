# AudioBridge

Ultra-low-latency, zero-loss audio streaming from Windows PC to Android over **Wi-Fi (UDP)** or **USB (ADB reverse tunnel)** with automated PC speaker silencing.

---

## Features

- **Near-Zero Latency Pipeline:**
  - **Windows:** WASAPI loopback capture running on an MMCSS "Pro Audio" high-priority thread with 1 ms polling and native 5 ms packet framing.
  - **Transport:**
    - **Wi-Fi:** High-speed UDP datagrams + Android `WIFI_MODE_FULL_LOW_LATENCY` lock.
    - **USB:** TCP streaming with `TCP_NODELAY` over automated `adb reverse` tunnel (typically 25–40 ms total latency).
  - **Android:** Native C++ engine (NDK) using **Google Oboe / AAudio** in Exclusive Low-Latency mode, lock-free ring jitter buffer, and a drift-correcting cubic resampler (PI controller) to eliminate clock drift without clicks or buffer growth.
- **Silent PC Speakers While Streaming:**
  - Automatically switches Windows default output to **VB-CABLE** (virtual audio driver) when streaming starts, restoring your physical speakers when disconnected.
  - Built-in crash recovery: restores original endpoint and volume settings even if the app crashes or the PC restarts.
  - Driver fallback: mutes speakers if VB-CABLE is not installed.
- **Zero Configuration Discovery:**
  - PC broadcasts UDP beacons on port 47801; Android automatically discovers PCs on the local Wi-Fi.
- **Live Telemetry & Jitter Tuning:**
  - Real-time display of end-to-end latency, jitter buffer size, hardware output latency, RTT, drift PPM, packet loss, and underruns.
  - Adjustable slider to tune between **Lowest Latency (5 ms)** and **Most Stable (60 ms)**.

---

## Requirements

- **Windows:**
  - Windows 10/11 (64-bit)
  - .NET 8.0 Runtime or SDK
  - (Recommended) [VB-CABLE](https://vb-audio.com/Cable/) for silent-speaker streaming
- **Android:**
  - Android 10+ (API 29+)
  - For USB mode: USB Debugging enabled in Developer Options

---

## How to Build

### Windows PC App
```powershell
cd windows
dotnet build AudioBridge.sln -c Release
```
The compiled tray executable will be at `windows\AudioBridge.Tray\bin\Release\net8.0-windows\AudioBridge.exe`.

### Android App
```powershell
cd android
.\gradlew assembleDebug
```
The compiled APK will be at `android\app\build\outputs\apk\debug\app-debug.apk`.

---

## How to Run

1. **Start PC Tray App:**
   - Run `AudioBridge.exe`. An icon will appear in your system tray and the status window will open.
   - It will begin capturing your default audio device and listening for Android connections.
2. **Wi-Fi Mode:**
   - Open AudioBridge on Android.
   - Your PC will appear in the list under "Discovered PCs on Wi-Fi". Tap **Connect**.
3. **USB Mode (Lowest Latency):**
   - Connect your phone to the PC with a USB cable.
   - Enable **USB Debugging** on your phone and accept any RSA key prompt.
   - In the Android app, select **USB (ADB Tunnel)** and tap **Connect over USB**.

---

## Port Reference

| Port | Protocol | Purpose |
|---|---|---|
| **47800** | UDP | Audio and control stream (Wi-Fi) |
| **47801** | UDP | Discovery beacons (Broadcast) |
| **47802** | TCP | Audio and control stream (USB / ADB reverse) |
