namespace WetFlow;

public class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private Label _hotkeyLabel = null!;
    private Button _captureButton = null!;
    private ComboBox _modelCombo = null!;
    private Button _saveButton = null!;
    private int _pendingVKey;
    private bool _capturing;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        _pendingVKey = settings.HotkeyVKey;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "WetFlow Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(320, 160);

        var hotkeyGroupLabel = new Label { Text = "Push-to-talk hotkey:", Left = 16, Top = 16, Width = 140, AutoSize = true };
        _hotkeyLabel = new Label { Left = 16, Top = 38, Width = 180, Height = 24, BorderStyle = BorderStyle.FixedSingle, TextAlign = ContentAlignment.MiddleLeft };
        _hotkeyLabel.Text = ((Keys)_pendingVKey).ToString();
        _captureButton = new Button { Text = "Change…", Left = 204, Top = 36, Width = 96, Height = 26 };
        _captureButton.Click += OnCapture;

        var modelGroupLabel = new Label { Text = "Whisper model:", Left = 16, Top = 76, Width = 140, AutoSize = true };
        _modelCombo = new ComboBox { Left = 16, Top = 98, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        _modelCombo.Items.AddRange(new object[] { "tiny", "base", "small", "medium" });
        _modelCombo.SelectedItem = _settings.WhisperModel;
        if (_modelCombo.SelectedIndex < 0) _modelCombo.SelectedIndex = 1;

        _saveButton = new Button { Text = "Save", Left = 220, Top = 120, Width = 80, Height = 28, DialogResult = DialogResult.OK };
        _saveButton.Click += OnSave;
        AcceptButton = _saveButton;

        Controls.AddRange(new Control[] { hotkeyGroupLabel, _hotkeyLabel, _captureButton, modelGroupLabel, _modelCombo, _saveButton });
        KeyPreview = true;
    }

    private void OnCapture(object? sender, EventArgs e)
    {
        _capturing = true;
        _captureButton.Text = "Press a key…";
        _captureButton.Enabled = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_capturing)
        {
            _pendingVKey = (int)e.KeyCode;
            _hotkeyLabel.Text = e.KeyCode.ToString();
            _captureButton.Text = "Change…";
            _captureButton.Enabled = true;
            _capturing = false;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnSave(object? sender, EventArgs e)
    {
        _settings.HotkeyVKey = _pendingVKey;
        _settings.WhisperModel = _modelCombo.SelectedItem?.ToString() ?? "base";
        _settings.Save();
    }
}
