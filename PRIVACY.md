# 🛡️ Privacy Policy & Security Guarantee

**Product:** Wireless Bark  
**Published by:** TAB inc.  
**Effective Date:** 2026

At **TAB inc.**, our core philosophy is simple: **Your audio, your hardware, and your local network belong strictly to you.**

---

## 1. Zero Telemetry & Zero Analytics
* **Wireless Bark collects NO telemetry**: There are zero tracking beacons, analytics scripts, crash report uploaders, advertising SDKs, or usage statistics embedded in either the Android application or the Windows PC software.
* **No Cloud Relay Servers**: Wireless Bark never communicates with any external TAB inc. server or cloud provider. All audio streaming and discovery exist 100% locally within your home Wi-Fi Local Area Network (LAN) or direct USB cable.
* **No Account Required**: You will never be asked to create an account, sign in, enter an email address, or provide personal details.

## 2. Audio Stream Privacy
* Captured PC audio is processed exclusively in volatile RAM and streamed in real-time packets directly to your Android device.
* Audio streams are **never recorded, never saved to disk, and never transmitted over the internet**.
* When streaming ceases, the audio buffers in RAM are discarded immediately.

## 3. Network Discovery
* Automatic discovery operates solely via local UDP broadcasts within your local subnet (`255.255.255.255:47801`).
* Discovery beacons contain only your computer's local hostname and audio port.
* Broadcast packets never leave your local router or reach the wider internet.

## 4. Hardware Integrity & Control
* The bundled VB-CABLE virtual audio driver is used exclusively to facilitate clean loopback capture while preventing audio from physically playing out of your PC speakers during a stream.
* Physical audio device endpoints and volumes are automatically restored upon disconnection or exit.
* No third-party bloatware, background update daemons, or search hijackers are included.

---
*For support or inquiries: contact TAB inc. via [GitHub Issues](https://github.com/Tr1pAc1d/Wireless_Bark/issues).*
