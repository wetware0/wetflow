# wetflow Implementation Plan

## What We're Building

A C# .NET 8 Windows system tray app that captures microphone audio while a configurable hotkey (default: Right Shift) is held, transcribes it locally using Whisper.net, and injects the result at the cursor. No API keys or internet required.

## Stack

- `.NET 8`, `net8.0-windows`, WinForms (`UseWindowsForms`)
- `NAudio` — WasapiCapture for mic input, resampled to 16kHz mono
- `Whisper.net` + `Whisper.net.Runtime` — local GGML model, auto-downloaded on first run
- `SetWindowsHookEx(WH_KEYBOARD_LL)` — global push-to-talk key hook
- `SendInput` (Unicode) → clipboard+Ctrl+V fallback for text injection

## Pipeline

```
[Right Shift ↓] → KeyboardHook → AudioRecorder.Start() → tray icon red
[Right Shift ↑] → KeyboardHook → AudioRecorder.Stop()  → WAV bytes
                                → Transcriber.TranscribeAsync() → string
                                → TextInjector.InjectAsync()
                                   ├─ SendInput Unicode
                                   └─ fallback: Clipboard + Ctrl+V
                                → tray icon idle
```

## Tasks

- [ ] **Task 1: Project scaffold**
  - Create `WetFlow.sln` and `src/WetFlow.csproj` (`net8.0-windows`, `WinExe`, WinForms)
  - Add NuGet refs: `NAudio`, `Whisper.net`, `Whisper.net.Runtime`
  - Add placeholder icon files (`mic_idle.ico`, `mic_recording.ico`) as embedded resources

- [ ] **Task 2: AppSettings**
  - `AppSettings.cs` — POCO with `HotkeyVKey` (default: `Keys.RShiftKey`) and `WhisperModel` (default: `"base"`)
  - JSON load/save to `%APPDATA%\wetflow\settings.json`

- [ ] **Task 3: KeyboardHook**
  - `KeyboardHook.cs` — `SetWindowsHookEx(WH_KEYBOARD_LL)` global hook
  - Fires `KeyDown` / `KeyUp` events for the configured key
  - Suppresses key-down while recording (returns `(IntPtr)1`, skips `CallNextHookEx`)
  - Sends synthetic key-up on release to clear shift state

- [ ] **Task 4: AudioRecorder**
  - `AudioRecorder.cs` — `WasapiCapture`, buffers PCM to `MemoryStream`
  - On `Stop()`: resample to 16kHz/16-bit/mono via `WaveFormatConversionStream`, write temp WAV file, return path

- [ ] **Task 5: Transcriber**
  - `Transcriber.cs` — `WhisperFactory` + `WhisperProcessor`, lazy-loaded on first use
  - `TranscribeAsync(wavPath) → string`
  - On first load: `WhisperGgmlDownloader` pulls model from HuggingFace; fires `ModelLoading` event for tray tip

- [ ] **Task 6: TextInjector**
  - `TextInjector.cs` — `InjectAsync(text)`:
    1. Try `SendInput` with `KEYEVENTF_UNICODE` per character
    2. If `SendInput` returns 0 (blocked), fall back to `Clipboard.SetText(text)` + `SendInput` Ctrl+V

- [ ] **Task 7: SettingsForm**
  - `SettingsForm.cs` — WinForms dialog
  - Hotkey capture control (any keypress updates the binding)
  - Model size dropdown (tiny/base/small/medium)
  - Save button writes to `AppSettings` and persists to disk

- [ ] **Task 8: TrayApp**
  - `TrayApp.cs` — `ApplicationContext` subclass
  - `NotifyIcon` with `mic_idle.ico`; context menu: Settings | About | Exit
  - Wires `KeyboardHook.KeyDown` → `AudioRecorder.Start()` + icon swap
  - Wires `KeyboardHook.KeyUp` → `AudioRecorder.Stop()` → `Transcriber` → `TextInjector` (all async, off UI thread)
  - Shows "Transcribing…" tooltip during transcription; shows error balloon on failure

- [ ] **Task 9: Program.cs**
  - Single-instance mutex guard (exit silently if already running)
  - `Application.EnableVisualStyles()`, `Application.Run(new TrayApp())`

- [ ] **Task 10: Icons**
  - Generate two simple 16x32 `.ico` files: grey mic (idle), red mic (recording)
  - Embed as resources in `.csproj`

- [ ] **Task 11: Build & smoke test**
  - `dotnet build` — zero errors
  - App appears in tray, right-click menu works
  - Hold Right Shift → icon turns red → release → text injected in Notepad

## Success Criteria

- [ ] App starts and sits in tray, no visible window, <50MB RAM
- [ ] Hold Right Shift → icon red, mic records; no stray Shift characters in foreground app
- [ ] Release → transcript injects at cursor in Notepad, VS Code, Chrome
- [ ] First run downloads Whisper base model with tray notification
- [ ] Settings dialog lets user rebind hotkey and change model size; persists across restarts
- [ ] Single instance: second launch exits silently
- [ ] Exit from tray menu cleanly unhooks and quits

## Key Risks

| Risk | Mitigation |
|---|---|
| Hook thread stall causes input lag | Recording/transcription on `Task`; hook callback returns immediately |
| Whisper CPU latency 2-3s | Expected for push-to-talk; icon shows state clearly |
| `SendInput` blocked by UAC-elevated window | Clipboard fallback; document this limitation |
| Right Shift bleeds Shift state after release | Suppress key-down during record; send synthetic key-up on release |
| NAudio format mismatch | Resample to 16kHz/16-bit/mono before passing to Whisper |
| Model download fails (no internet on first run) | Show error balloon; retry available from Settings |
