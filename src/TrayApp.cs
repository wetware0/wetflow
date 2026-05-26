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
    private volatile bool _busy;
    private bool _recording;
    private CancellationTokenSource? _cts;
    private readonly SynchronizationContext _uiContext;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "wetflow", "error.log");

    public TrayApp()
    {
        Exception? settingsLoadError;
        (_settings, settingsLoadError) = AppSettings.Load();

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
            WarnSettingsLoadFailed(settingsLoadError);

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
        overlayToggle.CheckedChanged += (s, _) =>
        {
            _settings.ShowOverlay = ((ToolStripMenuItem)s!).Checked;
            _settings.Save();
            if (!_settings.ShowOverlay)
                _overlay.HideOverlay();
            else if (_recording)
                _overlay.ShowRecording();
        };
        menu.Items.Add(overlayToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    private void OnKeyDown()
    {
        if (_busy) return;
        if (_recording)
        {
            if (_settings.UseToggleMode) OnKeyUp(intentionalStop: true);
            return;
        }
        try
        {
            _recording = true;
            _recorder.Start();
            _hook.IsCancellable = true;
            _tray.Icon = _recordingIcon;
            _tray.Text = "WetFlow — recording…";
            if (_settings.ShowOverlay) _overlay.ShowRecording();
        }
        catch (Exception ex)
        {
            _recording = false;
            LogError(ex);
            _tray.ShowBalloonTip(5000, "WetFlow Error", $"Could not start recording: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void OnKeyUp() => OnKeyUp(intentionalStop: false);

    private void OnKeyUp(bool intentionalStop)
    {
        if (_busy || !_recording) return;
        if (_settings.UseToggleMode && !intentionalStop) return;
        _recording = false;
        _busy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            string? wavPath = null;
            try
            {
                wavPath = _recorder.Stop();
                if (wavPath == null) return;

                _tray.Text = "WetFlow — transcribing…";
                if (_settings.ShowOverlay) _overlay.ShowTranscribing();

                var text = await _transcriber.TranscribeAsync(wavPath, _settings.WhisperModel,
                    _settings.ShortPauseSecs, _settings.LongPauseSecs, token);
                File.Delete(wavPath);
                wavPath = null;

                if (!string.IsNullOrWhiteSpace(text))
                    await TextInjector.InjectAsync(text);
            }
            catch (OperationCanceledException)
            {
                // Escape pressed — discard without injecting
            }
            catch (Exception ex)
            {
                LogError(ex);
                _tray.ShowBalloonTip(8000, "WetFlow Error", $"{ex.Message} (see {LogPath})", ToolTipIcon.Error);
            }
            finally
            {
                if (wavPath != null)
                    try { File.Delete(wavPath); } catch (Exception) { }
                if (_settings.ShowOverlay) _overlay.HideOverlay();
                _hook.IsCancellable = false;
                _cts?.Dispose();
                _uiContext.Post(_ => {
                    _tray.Icon = _idleIcon;
                    _tray.Text = IdleTrayText;
                }, null);
                _busy = false;
            }
        });
    }

    private void OnCancelled()
    {
        if (_recording)
        {
            // Cancelled during recording phase — stop recorder, skip transcription
            _recording = false;
            _hook.IsCancellable = false;
            var wavPath = _recorder.Stop();
            if (wavPath != null)
                try { File.Delete(wavPath); } catch (Exception) { }
            if (_settings.ShowOverlay) _overlay.HideOverlay();
            _uiContext.Post(_ => {
                _tray.Icon = _idleIcon;
                _tray.Text = IdleTrayText;
            }, null);
        }
        else if (_busy)
        {
            // Cancelled during transcription phase — CancellationToken stops the pipeline
            _cts?.Cancel();
        }
    }

    private void OnOverlayRecordToggle(object? sender, EventArgs e)
    {
        if (_recording) OnKeyUp(intentionalStop: true);
        else OnKeyDown();
    }

    private void OnOverlayPositionChanged(object? sender, EventArgs e)
    {
        _settings.OverlayX = _overlay.Left;
        _settings.OverlayY = _overlay.Top;
        _settings.Save();
    }

    internal static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    private void WarnSettingsLoadFailed(Exception ex) =>
        _tray.ShowBalloonTip(5000, "WetFlow Warning",
            $"Failed to load settings, using defaults. (see {LogPath})",
            ToolTipIcon.Warning);

    private void OnSettings(object? sender, EventArgs e)
    {
        _hook.Uninstall();
        using var form = new SettingsForm(_settings);
        form.ShowDialog();
        var (reloadedSettings, reloadError) = AppSettings.Load();
        _settings = reloadedSettings;
        if (reloadError != null)
            WarnSettingsLoadFailed(reloadError);

        // Rebind hook to the (possibly changed) hotkey.
        _hook.Dispose();
        _hook = new KeyboardHook(_settings.HotkeyVKey);
        _hook.KeyDown += OnKeyDown;
        _hook.KeyUp += OnKeyUp;
        _hook.Cancelled += OnCancelled;
        _hook.Install();

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
