# WetFlow

Push-to-talk transcription for Windows. Hold **F12**, speak, release — transcribed text is injected at the cursor. Runs entirely locally; no API key or internet connection required after first launch.

## Download

Pre-built releases for Windows 10/11 (x64) are available on the [Releases page](https://github.com/wetware0/wetflow/releases). Download the latest `WetFlow-vX.Y.Z-win-x64.zip`, extract it, and run `WetFlow.exe`. No .NET installation required.

## How it works

Push-to-talk (default):
```
[F12 ↓] → record mic  →  [F12 ↑] → Whisper transcribes → text injected at cursor
```

Toggle mode:
```
[F12 ↓] → record mic  →  [F12 ↓ again] → Whisper transcribes → text injected at cursor
```

Audio is captured via WASAPI, resampled to 16 kHz mono, and transcribed by [Whisper.net](https://github.com/sandrohanea/whisper.net) using a local GGML model downloaded on first use (~150 MB for the default `base` model). Text is injected via `SendInput` (Unicode), with a clipboard + Ctrl+V fallback.

## Requirements

To **run** (pre-built release):
- Windows 10/11 (x64)
- A microphone

To **build from source**:
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)

## Build & run

```powershell
git clone https://github.com/wetware0/wetflow.git
cd wetflow
dotnet build src/WetFlow.csproj -c Release
.\src\bin\Release\net8.0-windows\WetFlow.exe
```

On first use, the Whisper `base` model (~150 MB) is downloaded automatically to `%APPDATA%\wetflow\models\`. Subsequent launches are instant.

## Run on login

Right-click the tray icon and choose **Settings**, or create a startup shortcut:

**Pre-built release** (replace path with where you extracted the ZIP):
```powershell
$exe = "C:\Path\To\WetFlow\WetFlow.exe"
$lnk = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\WetFlow.lnk"
$wsh = New-Object -ComObject WScript.Shell
$s = $wsh.CreateShortcut($lnk); $s.TargetPath = $exe; $s.Save()
```

**Build from source** (run from repo root after building):
```powershell
$exe = "$PWD\src\bin\Release\net8.0-windows\WetFlow.exe"
$lnk = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\WetFlow.lnk"
$wsh = New-Object -ComObject WScript.Shell
$s = $wsh.CreateShortcut($lnk); $s.TargetPath = $exe; $s.Save()
```

## Settings

Right-click the tray icon → **Settings**:

| Setting | Default | Notes |
|---|---|---|
| Hotkey | F12 | Any key; modifier keys (Shift, Ctrl, …) work but can cause sticky-key behavior |
| Whisper model | `base` | `tiny` is faster; `small` / `medium` are more accurate but slower and larger |
| Short pause (sec) | `0.5` | Gap between Whisper segments that inserts a single newline (`\n`) in the output |
| Long pause (sec) | `1.5` | Gap between segments that inserts a blank line (`\n\n`); gaps below short pause are joined with a space |
| Toggle mode | Off | When on: press once to start, press again to stop. When off: hold to record, release to transcribe |

Settings are saved to `%APPDATA%\wetflow\settings.json`.

## Troubleshooting

Errors are logged to `%APPDATA%\wetflow\error.log` with full stack traces.

| Symptom | Likely cause |
|---|---|
| No transcription, no error | Recording was too short (<200 ms) — speak for longer before releasing (push-to-talk) or pressing again (toggle mode) |
| Tray shows "Downloading…" for a long time | First-run model download; check your internet connection |
| Text injected in wrong case | Target app is intercepting modifier keys — switch to a non-modifier hotkey |
| App won't start (second instance) | Already running — check the system tray |
| "WetFlow Warning" balloon on startup or after settings save | `settings.json` is corrupt or unreadable — app is using defaults. Check `%APPDATA%\wetflow\error.log` for details; delete `settings.json` to reset to defaults |

## Project structure

```
src/
  AppSettings.cs    — settings model, JSON load/save
  AudioRecorder.cs  — WASAPI capture, resample to 16 kHz mono WAV
  KeyboardHook.cs   — global low-level keyboard hook (WH_KEYBOARD_LL)
  Program.cs        — entry point, single-instance mutex
  SettingsForm.cs   — hotkey capture + model selection dialog
  TextInjector.cs   — SendInput Unicode injection, clipboard fallback
  Transcriber.cs    — Whisper.net local transcription, model auto-download
  TrayApp.cs        — orchestrator, tray icon, pipeline coordination
```

## Releasing

To publish a new release, push a version tag:

```powershell
git tag v1.0.1
git push origin v1.0.1
```

GitHub Actions will run tests, build a self-contained `win-x64` ZIP, and create a GitHub release automatically.

## License

MIT
