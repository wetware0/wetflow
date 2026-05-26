using System.Runtime.InteropServices;

namespace WetFlow;

public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;
    internal const int VK_ESCAPE = 0x1B;

    public event Action? KeyDown;
    public event Action? KeyUp;
    public event Action? Cancelled;
    public volatile bool IsCancellable;

    private readonly int _vKey;
    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;
    private bool _recording;

    public KeyboardHook(int vKey)
    {
        _vKey = vKey;
        _proc = HookCallback;
    }

    public void Install()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    internal IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vkCode = Marshal.ReadInt32(lParam);
            var msg = (int)wParam;
            if (vkCode == _vKey)
            {
                if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && !_recording)
                {
                    _recording = true;
                    KeyDown?.Invoke();
                    return (IntPtr)1; // suppress
                }
                if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && _recording)
                {
                    _recording = false;
                    KeyUp?.Invoke();
                    return (IntPtr)1; // suppress
                }
                // Suppress repeat key-down events while recording
                if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && _recording)
                    return (IntPtr)1;
            }
            else if (vkCode == VK_ESCAPE && IsCancellable)
            {
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    Cancelled?.Invoke();
                return (IntPtr)1; // suppress both keydown and keyup
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string lpModuleName);
}
