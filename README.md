# LAN Transfer

**English** · [Português](README.pt-BR.md)

Send files, folders and text between Windows PCs, Android phones, Linux and Mac on the same network (Wi-Fi or cable). No cloud, no sign-up, no USB cable.

## Download

Get the latest version from **[Releases](../../releases/latest)**:

| System | File |
|---|---|
| Windows 10/11 (64-bit) | `LANTransfer-Setup-5.0.0.exe` |
| Android 10 or newer | `LANTransfer-5.0.0.apk` |
| Linux (64-bit) | `LANTransfer-5.0.0-linux-x64.tar.gz` |
| Mac with Intel | `LANTransfer-5.0.0-macos-intel.tar.gz` |
| Mac with Apple Silicon | `LANTransfer-5.0.0-macos-apple-silicon.tar.gz` |

On Android, allow "install unknown apps" for your browser or file manager when opening the APK.
On Linux and Mac, extract the archive and run `./lan-transfer` in a terminal. See the [Linux/Mac README](linux-mac/README.md), which also explains how to allow the app on macOS.

The app interface is available in Portuguese and English.

## Features

- **Finds the other devices automatically** on the network, even with more than one router.
- Each device **shares** files and folders, and the others see the list and download what they want. On the PC, files are shared in place, without copying.
- **Direct send**: send files or text to another device, which accepts or declines.
- Speed, time left, pause, cancel and **resume interrupted downloads**.
- Synced folders, thumbnails, preview and history.
- Optional **PIN** and "trusted devices only" mode.
- **HTTPS** between the apps, with the certificate checked against the fingerprint announced during discovery.
- **QR code** for people without the app: opens a page in the browser to download and upload.
- Light/dark theme and Portuguese/English.
- Windows: tray icon, start with Windows and "Disponibilizar no LAN Transfer" (share) in the Explorer right-click menu.

## Project layout

| Folder | Contents |
|---|---|
| [`windows`](windows) | WPF app in C# (.NET 8, WPF-UI, Kestrel, QRCoder) and Inno Setup installer |
| [`android`](android) | Kotlin app with Jetpack Compose (Material 3) and OkHttp |
| [`linux-mac`](linux-mac) | Single Python 3.8+ script using only the standard library, with a browser UI. Executables are built by GitHub Actions |

## Building

**Windows** (needs .NET SDK 8 and Inno Setup 6):

```powershell
cd windows
powershell -ExecutionPolicy Bypass -File build.ps1
```

The installer ends up in `windows/output/`.

**Android** (needs the Android SDK and JDK 17):

```powershell
cd android
powershell -ExecutionPolicy Bypass -File build-apk.ps1
```

Or, on any system, `./gradlew assembleRelease` with `sdk.dir` set in `local.properties`. The APK ends up in `android/output/`. The release build is signed with the local debug key so it can be installed directly.

**Linux/Mac**: you can run it directly with `python3 linux-mac/lan_transfer.py`. The executables are built by the [`desktop-builds.yml`](.github/workflows/desktop-builds.yml) workflow with PyInstaller.

## Network and firewall

The apps use **UDP port 48555** to find each other and **TCP 8000 to 8019** (HTTP) plus the same range +100 (HTTPS) for transfers. The Windows installer opens these in the firewall. On Linux with `ufw`, allow these ports manually.

## License

[MIT](LICENSE)
