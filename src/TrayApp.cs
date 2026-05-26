namespace WetFlow;

public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private KeyboardHook _hook;
    private readonly AudioRecorder _recorder;
    private readonly Transcriber _transcriber;
    private AppSettings _settings;
    private Icon _idleIcon = null!;
    private Icon _recordingIcon = null!;
    private bool _busy;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "wetflow", "error.log");

    public TrayApp()
    {
        _settings = AppSettings.Load();

        _idleIcon = LoadIcon("WetFlow.Resources.mic_idle.ico");
        _recordingIcon = LoadIcon("WetFlow.Resources.mic_recording.ico");

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = IdleTrayText,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _recorder = new AudioRecorder();
        _transcriber = new Transcriber();
        _transcriber.StatusChanged += msg => _tray.Text = $"WetFlow — {msg}";

        _hook = new KeyboardHook(_settings.HotkeyVKey);
        _hook.KeyDown += OnKeyDown;
        _hook.KeyUp += OnKeyUp;
        _hook.Install();
    }

    private string IdleTrayText => $"WetFlow — hold {(Keys)_settings.HotkeyVKey} to dictate";

    private static Icon LoadIcon(string resourceName)
    {
        var asm = typeof(TrayApp).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream != null) return new Icon(stream);
        // Fallback: generate a simple 16x16 icon programmatically
        return SystemIcons.Application;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, OnSettings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    private void OnKeyDown()
    {
        if (_busy) return;
        _recorder.Start();
        _tray.Icon = _recordingIcon;
        _tray.Text = "WetFlow — recording…";
    }

    private void OnKeyUp()
    {
        if (_busy) return;
        _busy = true;
        Task.Run(async () =>
        {
            try
            {
                var wavPath = _recorder.Stop();
                if (wavPath == null) return;

                _tray.Text = "WetFlow — transcribing…";
                var text = await _transcriber.TranscribeAsync(wavPath, _settings.WhisperModel);
                File.Delete(wavPath);

                if (!string.IsNullOrWhiteSpace(text))
                    await TextInjector.InjectAsync(text);
            }
            catch (Exception ex)
            {
                LogError(ex);
                _tray.ShowBalloonTip(8000, "WetFlow Error", $"{ex.Message} (see {LogPath})", ToolTipIcon.Error);
            }
            finally
            {
                _tray.Icon = _idleIcon;
                _tray.Text = IdleTrayText;
                _busy = false;
            }
        });
    }

    private static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    private void OnSettings(object? sender, EventArgs e)
    {
        _hook.Uninstall();
        using (var form = new SettingsForm(_settings))
            form.ShowDialog();
        _settings = AppSettings.Load();

        // Rebind hook to the (possibly changed) hotkey.
        _hook.Dispose();
        _hook = new KeyboardHook(_settings.HotkeyVKey);
        _hook.KeyDown += OnKeyDown;
        _hook.KeyUp += OnKeyUp;
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
            _tray.Dispose();
            _idleIcon.Dispose();
            _recordingIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
