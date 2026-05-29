using System.Diagnostics;

namespace WetFlow;

public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private KeyboardHook _hook;
    private readonly AudioRecorder _recorder;
    private readonly Transcriber _transcriber;
    private readonly OverlayForm _overlay;
    private AppSettings _settings;
    private Icon _idleIcon = null!;
    private Icon _recordingIcon = null!;
    private readonly RecordingStateMachine _sm;
    private CancellationTokenSource? _cts;
    private readonly SynchronizationContext _uiContext;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "wetflow", "error.log");
    private static bool _logDirCreated;

    private static readonly string FailedAudioDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "wetflow", "failed-audio");

    public TrayApp()
    {
        (_settings, var settingsLoadError) = AppSettings.Load();

        _sm = new RecordingStateMachine(_settings.UseToggleMode);
        _sm.RecordingStarted += OnRecordingStarted;
        _sm.StoppedForTranscription += OnStoppedForTranscription;
        _sm.RecordingCancelled += OnRecordingCancelled;
        _sm.TranscriptionCancellationRequested += () => _cts?.Cancel();

        _idleIcon = LoadIcon("WetFlow.Resources.mic_idle.ico");
        _recordingIcon = LoadIcon("WetFlow.Resources.mic_recording.ico");

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = IdleTrayText,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        if (settingsLoadError != null)
            WarnSettingsLoadFailed();

        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _recorder = new AudioRecorder();
        _transcriber = new Transcriber();
        _transcriber.StatusChanged += msg => _tray.Text = $"WetFlow — {msg}";

        _overlay = new OverlayForm();
        _overlay.RecordToggleRequested += OnOverlayRecordToggle;
        _overlay.PositionChanged += OnOverlayPositionChanged;
        _recorder.VolumeChanged += level => _overlay.UpdateVolume(level);
        PositionOverlay();

        _hook = new KeyboardHook(_settings.HotkeyVKey);
        _hook.KeyDown += OnKeyDown;
        _hook.KeyUp += OnKeyUp;
        _hook.Cancelled += OnCancelled;
        _hook.Install();
    }

    private string IdleTrayText => _settings.UseToggleMode
        ? $"WetFlow — press {(Keys)_settings.HotkeyVKey} to dictate"
        : $"WetFlow — hold {(Keys)_settings.HotkeyVKey} to dictate";

    private void PositionOverlay()
    {
        if (_settings.OverlayX >= 0 && _settings.OverlayY >= 0)
        {
            _overlay.Location = new Point(_settings.OverlayX, _settings.OverlayY);
        }
        else
        {
            var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            _overlay.Location = new Point(screen.Right - _overlay.Width - 24, screen.Bottom - _overlay.Height - 24);
        }
    }

    private static Icon LoadIcon(string resourceName)
    {
        var asm = typeof(TrayApp).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName);
        return stream != null ? new Icon(stream) : SystemIcons.Application;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, OnSettings);
        var overlayToggle = new ToolStripMenuItem("Show overlay") { Checked = _settings.ShowOverlay, CheckOnClick = true };
        overlayToggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowOverlay = overlayToggle.Checked;
            _settings.Save();
            if (!_settings.ShowOverlay)
                _overlay.HideOverlay();
            else if (_sm.CurrentState == RecordingStateMachine.State.Recording)
                _overlay.ShowRecording();
        };
        menu.Items.Add(overlayToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    private void OnKeyDown() => _sm.HandleKeyDown();

    private void OnKeyUp() => _sm.HandleKeyUp();

    private void OnCancelled() => _sm.HandleCancelled();

    private void OnOverlayRecordToggle(object? sender, EventArgs e) => _sm.HandleOverlayToggle();

    private void OnRecordingStarted()
    {
        try
        {
            _recorder.Start();
            _hook.IsCancellable = true;
            _tray.Icon = _recordingIcon;
            _tray.Text = "WetFlow — recording…";
            if (_settings.ShowOverlay) _overlay.ShowRecording();
        }
        catch (Exception ex)
        {
            _sm.HandleStartFailed();
            LogError(ex);
            _tray.ShowBalloonTip(5000, "WetFlow Error", $"Could not start recording: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void OnStoppedForTranscription()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            string? wavPath = null;
            string? text = null;
            bool wasCancelled = false;
            Exception? transcriptionError = null;
            try
            {
                var sw = Stopwatch.StartNew();
                wavPath = _recorder.Stop();
                if (wavPath == null)
                    return;
                Log($"[TIMING] recorder-stop→wav-ready: {sw.ElapsedMilliseconds} ms");

                _tray.Text = "WetFlow — transcribing…";
                if (_settings.ShowOverlay) _overlay.ShowTranscribing();

                sw.Restart();
                // On first run this includes model download; subsequent calls measure transcription only.
                text = await _transcriber.TranscribeAsync(wavPath, _settings.WhisperModel,
                    _settings.ShortPauseSecs, _settings.LongPauseSecs, _settings.UseGpu, token);
                Log($"[TIMING] wav-ready→transcription-complete: {sw.ElapsedMilliseconds} ms");

                if (!string.IsNullOrWhiteSpace(text))
                    await TextInjector.InjectAsync(text, _settings.OutputMode);
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                // Escape pressed — discard without injecting, audio deleted normally
            }
            catch (Exception ex)
            {
                LogError(ex);
                transcriptionError = ex;
            }
            finally
            {
                if (wavPath != null)
                {
                    if (ShouldPreserveAudio(text, wasCancelled, transcriptionError))
                    {
                        var errorContext = transcriptionError != null
                            ? $"{transcriptionError.Message} (see {LogPath})"
                            : null;
                        SaveAudioForRecovery(wavPath, errorContext);
                    }
                    else
                        try { File.Delete(wavPath); } catch (Exception) { }
                }
                if (_settings.ShowOverlay) _overlay.HideOverlay();
                _hook.IsCancellable = false;
                _cts?.Dispose();
                _uiContext.Post(_ =>
                {
                    _tray.Icon = _idleIcon;
                    _tray.Text = IdleTrayText;
                    _sm.HandleTranscriptionComplete();
                }, null);
            }
        });
    }

    internal static bool ShouldPreserveAudio(string? text, bool wasCancelled, Exception? transcriptionError)
        => transcriptionError != null || (!wasCancelled && string.IsNullOrWhiteSpace(text));

    private void OnRecordingCancelled()
    {
        _hook.IsCancellable = false;
        var wavPath = _recorder.Stop();
        if (wavPath != null)
            try { File.Delete(wavPath); } catch (Exception) { }
        if (_settings.ShowOverlay) _overlay.HideOverlay();
        _uiContext.Post(_ =>
        {
            _tray.Icon = _idleIcon;
            _tray.Text = IdleTrayText;
        }, null);
    }

    private void OnOverlayPositionChanged(object? sender, EventArgs e)
    {
        _settings.OverlayX = _overlay.Left;
        _settings.OverlayY = _overlay.Top;
        _settings.Save();
    }

    internal static void LogError(Exception ex) => Log($"{ex}{Environment.NewLine}");

    internal static void Log(string message)
    {
        try
        {
            if (!_logDirCreated) { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); _logDirCreated = true; }
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void WarnSettingsLoadFailed() =>
        _tray.ShowBalloonTip(5000, "WetFlow Warning",
            $"Failed to load settings, using defaults. (see {LogPath})",
            ToolTipIcon.Warning);

    private void SaveAudioForRecovery(string wavPath, string? errorContext = null)
    {
        try
        {
            Directory.CreateDirectory(FailedAudioDir);
            var dest = Path.Combine(FailedAudioDir, $"wetflow_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");
            File.Move(wavPath, dest);
            var isError = errorContext != null;
            var title = isError ? "WetFlow Error" : "WetFlow";
            var icon = isError ? ToolTipIcon.Error : ToolTipIcon.Info;
            var msg = isError ? $"{errorContext}\nAudio saved to {dest}" : $"Audio saved to {dest}";
            _tray.ShowBalloonTip(8000, title, msg, icon);
        }
        catch (Exception ex)
        {
            LogError(ex);
            _tray.ShowBalloonTip(8000, "WetFlow Warning",
                $"Could not save audio for recovery: {ex.Message} (see {LogPath})", ToolTipIcon.Warning);
            try { File.Delete(wavPath); } catch { }
        }
    }

    private void OnSettings(object? sender, EventArgs e)
    {
        _hook.Uninstall();
        using var form = new SettingsForm(_settings);
        form.ShowDialog();
        (_settings, var reloadError) = AppSettings.Load();
        if (reloadError != null)
            WarnSettingsLoadFailed();

        _hook.Dispose();
        _hook = new KeyboardHook(_settings.HotkeyVKey);
        _hook.KeyDown += OnKeyDown;
        _hook.KeyUp += OnKeyUp;
        _hook.Cancelled += OnCancelled;
        _hook.Install();

        _sm.UseToggleMode = _settings.UseToggleMode;
        _tray.Text = IdleTrayText;
    }

    private void ExitApp()
    {
        _hook.Uninstall();
        _tray.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hook.Dispose();
            _recorder.Dispose();
            _transcriber.Dispose();
            _overlay.Dispose();
            _tray.Dispose();
            _idleIcon.Dispose();
            _recordingIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
