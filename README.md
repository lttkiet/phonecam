# Phonecam

Phonecam streams an Android phone camera to a Windows receiver over Wi-Fi or USB debugging and exposes it as a Windows Media Foundation virtual camera.

## Build

Run `scripts/build.ps1` from PowerShell with the .NET 10 SDK, Java JDK, and Android SDK installed. It builds the Windows receiver and virtual camera components in `artifacts/windows/`, plus a signed Android debug APK at `artifacts/phonecam.apk`. Windows needs Windows 11 (build 22000 or newer) and the .NET 10 Desktop Runtime.

## Use

1. Install `artifacts/phonecam.apk` on the phone, open Phonecam, and grant camera access. The camera starts automatically.
2. On Windows, run `artifacts/windows/Phonecam.exe` and enter the phone address shown in the Android app. With USB debugging enabled, Phonecam creates the ADB forward when you connect. The Android server supports up to four simultaneous streams.
3. Use **Phone camera settings** in the Windows app to change the lens, supported capture size, frame-rate range, and JPEG quality. Settings are saved on the phone and can also be changed in its Phonecam app. The Windows virtual camera advertises the selected capture size (up to 1920×1080). Reopen the camera in your video app after changing resolution.
4. Click **Install Windows camera** once and approve the Windows administrator prompt. Keep Phonecam open and connected. Choose **Phonecam** as the camera in Windows video call apps.

The Android activity must remain open while streaming.
