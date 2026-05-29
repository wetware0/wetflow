using System.Runtime.InteropServices;

namespace WetFlow;

internal sealed class ClipboardMonitor : IDisposable
{
    public event Action? ContentChanged;

    private string? _watchedText;
    private readonly MessageWindow _window;

    internal ClipboardMonitor()
    {
        _window = new MessageWindow(OnClipboardUpdate);
    }

    // Call on UI thread only. Updates watched text; idempotent if already watching.
    internal void Watch(string text)
    {
        _watchedText = text;
        _window.Register();
    }

    internal void Stop()
    {
        _watchedText = null;
        _window.Unregister();
    }

    public void Dispose()
    {
        _watchedText = null;
        _window.Dispose();
    }

    private void OnClipboardUpdate()
    {
        if (_watchedText == null) return;
        try
        {
            var current = Clipboard.GetText();
            if (current != _watchedText)
            {
                _watchedText = null;
                _window.Unregister();
                ContentChanged?.Invoke();
            }
        }
        catch { }
    }

    private sealed class MessageWindow : NativeWindow, IDisposable
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private readonly Action _onUpdate;
        private bool _registered;

        internal MessageWindow(Action onUpdate)
        {
            _onUpdate = onUpdate;
            CreateHandle(new CreateParams());
        }

        internal void Register()
        {
            if (_registered) return;
            AddClipboardFormatListener(Handle);
            _registered = true;
        }

        internal void Unregister()
        {
            if (!_registered) return;
            RemoveClipboardFormatListener(Handle);
            _registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
                _onUpdate();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }
}
