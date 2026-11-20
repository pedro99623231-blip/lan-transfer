# LAN Transfer for Linux and macOS

[Português](LEIA-ME.md)

## Ready-to-run build (recommended)

Download the file for your system from the [Releases](https://github.com/pedro99623231-blip/lan-transfer/releases/latest) page:

| System | File |
|---|---|
| Linux (64-bit) | `LANTransfer-5.0.0-linux-x64.tar.gz` |
| Mac with Intel | `LANTransfer-5.0.0-macos-intel.tar.gz` |
| Mac with Apple Silicon (M1 and newer) | `LANTransfer-5.0.0-macos-apple-silicon.tar.gz` |

Extract it and run in a terminal:

```
./lan-transfer
```

**macOS:** the app is not signed by Apple, so the first time macOS will block it. Run this once in the extracted folder:

```
xattr -d com.apple.quarantine lan-transfer
```

## Running from source

Only Python 3.8 or newer is needed. Nothing gets installed.

```
python3 lan_transfer.py                 # shares the ~/LAN Transfer/Compartilhar folder
python3 lan_transfer.py photo.jpg Folder # also shares these files and folders
```

The app opens in your browser (http://127.0.0.1:8000/app). There you can:
- see the other devices running LAN Transfer (Windows PC, Android, other Mac/Linux) and download their files;
- send files and text to them;
- accept or decline what others want to send you (and copy received text).

Everything you receive goes to `~/Downloads/LAN Transfer`. Interrupted downloads resume where they stopped.
People without the app can open the address shown in the terminal in their browser.

To quit: Ctrl+C in the terminal. Use `--no-browser` to skip opening the browser.

Notes: this version talks without encryption (HTTP); the Windows and Android apps fall back to HTTP automatically with it. On Linux with a firewall (ufw), allow UDP port 48555 and TCP 8000-8019.
