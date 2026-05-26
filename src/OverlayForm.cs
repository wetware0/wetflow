namespace WetFlow;

public sealed class OverlayForm : Form
{
    private enum State { Recording, Transcribing }

    private State _state = State.Recording;
    private volatile float _volume;
    private readonly float[] _bars = new float[9];
    private int _dotTick;
    private readonly System.Windows.Forms.Timer _timer;
    private Point _dragOffset;
    private bool _wasDragged;
    private static readonly Random _rng = new();

    // Cached GDI objects — allocated once, reused every paint tick.
    private readonly SolidBrush _textBrush = new(Color.White);
    private readonly SolidBrush _dotBrush  = new(Color.LightSkyBlue);
    private readonly SolidBrush _barBrush  = new(Color.FromArgb(80, 220, 100));
    private readonly Font _labelFont = new("Segoe UI", 9f, FontStyle.Regular);
    private readonly Font _dotFont   = new("Segoe UI", 11f);

    public event EventHandler? RecordToggleRequested;
    public event EventHandler? PositionChanged;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(40, 40, 40);
        Opacity = 0.88;
        ClientSize = new Size(220, 60);
        StartPosition = FormStartPosition.Manual;
        Cursor = Cursors.SizeAll;

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += OnTick;
    }

    // Prevents the overlay from stealing keyboard focus when shown.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    public void ShowRecording()
    {
        if (InvokeRequired) { BeginInvoke(ShowRecording); return; }
        _state = State.Recording;
        _timer.Start();
        Show();
        BringToFront();
    }

    public void ShowTranscribing()
    {
        if (InvokeRequired) { BeginInvoke(ShowTranscribing); return; }
        _state = State.Transcribing;
        _volume = 0f;
    }

    public void HideOverlay()
    {
        if (InvokeRequired) { BeginInvoke(HideOverlay); return; }
        _timer.Stop();
        Hide();
    }

    public void UpdateVolume(float level)
    {
        _volume = level;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        AnimateBars();
        _dotTick++;
        Invalidate();
    }

    private void AnimateBars()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            float target = _state == State.Recording
                ? Math.Clamp(_volume * (0.5f + (float)_rng.NextDouble() * 0.8f), 0.05f, 1f)
                : 0.05f;
            _bars[i] += (target - _bars[i]) * 0.35f;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (_state == State.Recording)
        {
            g.DrawString("● Recording...", _labelFont, _textBrush, 8, 6);
            DrawBars(g);
        }
        else
        {
            string dots = ((_dotTick / 4) % 3) switch { 0 => "•", 1 => "• •", _ => "• • •" };
            g.DrawString("⟳ Transcribing...", _labelFont, _textBrush, 8, 6);
            g.DrawString(dots, _dotFont, _dotBrush, 8, 30);
        }
    }

    private void DrawBars(Graphics g)
    {
        int barWidth = 12;
        int gap = 5;
        int maxHeight = 22;
        int baseY = 56;
        int startX = 8;

        for (int i = 0; i < _bars.Length; i++)
        {
            int h = Math.Max(3, (int)(_bars[i] * maxHeight));
            int x = startX + i * (barWidth + gap);
            g.FillRectangle(_barBrush, x, baseY - h, barWidth, h);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragOffset = e.Location;
        _wasDragged = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _wasDragged = true;
        Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_wasDragged)
            PositionChanged?.Invoke(this, EventArgs.Empty);
        else
            RecordToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            base.OnFormClosing(e);
        }
        else
            base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _textBrush.Dispose();
            _dotBrush.Dispose();
            _barBrush.Dispose();
            _labelFont.Dispose();
            _dotFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
