using System.Runtime.InteropServices;

namespace WetFlow;

public static class TextInjector
{
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static async Task InjectAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Small delay so focus settles after key release
        await Task.Delay(80);

        if (!TrySendInput(text))
            await ClipboardFallbackAsync(text);
    }

    private static bool TrySendInput(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } }
            });
        }

        var arr = inputs.ToArray();
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        return sent == arr.Length;
    }

    private static async Task ClipboardFallbackAsync(string text)
    {
        string? previous = null;
        var tcs = new TaskCompletionSource();

        // Clipboard must be accessed on STA thread
        var thread = new Thread(() =>
        {
            try
            {
                previous = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                Clipboard.SetText(text);
            }
            finally
            {
                tcs.SetResult();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await tcs.Task;

        // Send Ctrl+V
        keybd_event(0x11, 0, 0, UIntPtr.Zero);          // Ctrl down
        keybd_event(0x56, 0, 0, UIntPtr.Zero);          // V down
        keybd_event(0x56, 0, 0x0002, UIntPtr.Zero);     // V up
        keybd_event(0x11, 0, 0x0002, UIntPtr.Zero);     // Ctrl up

        // Restore clipboard after paste settles
        await Task.Delay(300);
        if (previous != null)
        {
            var tcs2 = new TaskCompletionSource();
            var t2 = new Thread(() =>
            {
                try { Clipboard.SetText(previous); }
                finally { tcs2.SetResult(); }
            });
            t2.SetApartmentState(ApartmentState.STA);
            t2.Start();
            await tcs2.Task;
        }
    }
}
