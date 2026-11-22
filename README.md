# Nahr (نهر) — Download Manager

**Nahr** is a fast, modern, IDM-style desktop download manager for Windows built with **C# / .NET 10 / WPF / MVVM / SQLite**.

## Download & Install

Download the latest **`Nahr-Setup-1.3.2.exe`** from [**GitHub Releases**](../../releases/latest) (self-contained Windows x64 installer — no `.NET 10` runtime required).

## Features

- **Multi-Segment Accelerated Downloads** — Up to 32 parallel connections per file with adaptive `HTTP 429 / 503` concurrency scaling, automatic single-stream fallback, resume validation (`ETag` / `Last-Modified`), and 10 Hz live telemetry.
- **IDM-Style Multi-Tab Progress Window** — `Download status`, `Speed Limiter`, and `Options on completion` tabs with live connection map bar and per-connection table (`N. | Downloaded | Info`).
- **YouTube, Playlists & HLS (`.m3u8`) Streaming Sites** — Built-in pure-C# YouTube extractor (no `yt-dlp.exe` required), interactive **Playlist Video Picker** before download starts, dedicated **Video vs. Audio** mode switcher (`4K / 1080p / 720p / 480p / 360p MP4`, `MP3 320/192/128 kbps`, `M4A`), and multi-quality HLS variant discovery.
- **Browser Extension with Auto-Launch (`Native Messaging`)** — Manifest V3 extension (`browser-extension/`) for Chrome, Edge, Brave, and Firefox with an IDM-style floating video/audio download bar, in-browser quality menu, and **automatic background launch** even when Nahr is closed.
- **Compact High-Density List & Expandable Playlists** — Multi-select checkboxes with contextual bulk toolbar (`Resume`, `Pause`, `Cancel`, `Remove`, `Delete from disk`) and nested playlist containers.
- **Crash-Safe Persistence** — SQLite storage with durable pause/resume, automatic schema migrations, and optional auto-resume on startup.
- **Native Windows 11 Experience** — Light & Dark themes, full **Arabic (RTL) & English (LTR)** localization, silent tray startup with Windows (`--minimized`), floating active download tracker, clipboard URL monitoring, and drag-and-drop.

## Browser Extension Setup

1. Open `chrome://extensions` (or `edge://extensions`).
2. Enable **Developer mode**.
3. Click **Load unpacked** and select the `browser-extension` folder (included next to `Nahr.exe` after installation).
4. Nahr automatically registers the per-user Native Messaging Host (`com.nahr.downloader`) so browser downloads auto-launch Nahr silently in the background.

## Build, Test & Package Installer

```powershell
# Build the entire solution (Release)
dotnet build DownloadManager.sln -c Release

# Run the full verification suite (117 tests)
dotnet run -c Release --no-build --project tests/DownloadManager.Verification

# Build + verify + publish self-contained app (.\publish\Nahr.exe) + build installer (.\dist\Nahr-Setup-1.3.2.exe)
.\build-windows.ps1 -BuildInstaller
```

## Project Structure

```text
Nahr/
├─ DownloadManager.sln                Unified Visual Studio / .NET solution
├─ Directory.Build.props              Shared compiler & SDK configuration
├─ build-windows.ps1                  Build, test, publish, and installer script
├─ installer/Nahr.iss                 Inno Setup 6 Windows installer script
├─ ARCHITECTURE.md                    Technical architecture & engine design
├─ browser-extension/                 Chrome / Edge / Firefox Manifest V3 extension
├─ src/
│  ├─ DownloadManager.Core/           Domain models, state machine, PathSafety
│  ├─ DownloadManager.Engine/         Segmented downloader, HLS, YouTube, queue, speed limiter & extension server
│  ├─ DownloadManager.Data/           SQLite persistence layer (schema v2)
│  ├─ DownloadManager.Infrastructure/ Rolling file logger with secret scrubbing
│  ├─ DownloadManager.Presentation/   MVVM view-models, AR/EN localization, settings
│  └─ DownloadManager.App/            WPF UI, themes, tray, clipboard & Windows Native Messaging integration
└─ tests/
   ├─ DownloadManager.TestServer/     Local Kestrel HTTP test server with fault injection
   └─ DownloadManager.Verification/   117 unit, integration, SQLite, MVVM, XAML, HLS, YouTube & extension tests
```
