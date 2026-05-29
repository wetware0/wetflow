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

    public static async Task InjectAsync(string text, OutputMode outputMode = OutputMode.KeyboardAndClipboard)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Small delay so focus settles after key release
        await Task.Delay(80);

        switch (outputMode)
        {
            case OutputMode.KeyboardOnly:
                if (!TrySendInput(text))
                    throw new InvalidOperationException("SendInput failed — text could not be injected.");
                break;
            case OutputMode.ClipboardOnly:
                await SetClipboardAsync(text);
                break;
            case OutputMode.KeyboardAndClipboard:
                TrySendInput(text);
                await SetClipboardAsync(text);
                break;
        }
    }

    private static bool TrySendInput(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0x0D } } });
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0x0D, dwFlags = KEYEVENTF_KEYUP } } });
            }
            else if (ch != '\r')
            {
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
            }
        }

        var arr = inputs.ToArray();
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        return sent == arr.Length;
    }

    private static async Task SetClipboardAsync(string text)
    {
        var tcs = new TaskCompletionSource();

        // Clipboard must be accessed on STA thread
        var thread = new Thread(() =>
        {
            try
            {
                Clipboard.SetText(text);
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await tcs.Task;
    }
}
