using System.Runtime.InteropServices;
using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class KeyboardHookTests
{
    [Fact]
    public void IsCancellable_DefaultIsFalse()
    {
        // Does not install the hook — verifies default state only
        var hook = new KeyboardHook(0x70 /* F1 */);
        Assert.False(hook.IsCancellable);
    }

    [Fact]
    public void Cancelled_NotFiredWhenIsCancellableIsFalse()
    {
        var hook = new KeyboardHook(0x70);
        var fired = false;
        hook.Cancelled += () => fired = true;

        // IsCancellable is false by default — Cancelled must not have fired
        Assert.False(fired);
        Assert.False(hook.IsCancellable);
    }

    [Fact]
    public void HookCallback_EscapeKeyDown_WhenCancellable_SuppressesAndFiresCancelled()
    {
        var hook = new KeyboardHook(0x70 /* F1 */);
        hook.IsCancellable = true;
        var fired = false;
        hook.Cancelled += () => fired = true;

        var ptr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(ptr, KeyboardHook.VK_ESCAPE);
            var result = hook.HookCallback(0, (IntPtr)KeyboardHook.WM_KEYDOWN, ptr);
            Assert.Equal((IntPtr)1, result);
            Assert.True(fired);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void HookCallback_EscapeSysKeyDown_WhenCancellable_SuppressesAndFiresCancelled()
    {
        var hook = new KeyboardHook(0x70);
        hook.IsCancellable = true;
        var fired = false;
        hook.Cancelled += () => fired = true;

        var ptr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(ptr, KeyboardHook.VK_ESCAPE);
            var result = hook.HookCallback(0, (IntPtr)KeyboardHook.WM_SYSKEYDOWN, ptr);
            Assert.Equal((IntPtr)1, result);
            Assert.True(fired);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void HookCallback_EscapeKeyUp_WhenCancellable_SuppressesButDoesNotFireCancelled()
    {
        var hook = new KeyboardHook(0x70);
        hook.IsCancellable = true;
        var fired = false;
        hook.Cancelled += () => fired = true;

        var ptr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(ptr, KeyboardHook.VK_ESCAPE);
            var result = hook.HookCallback(0, (IntPtr)KeyboardHook.WM_KEYUP, ptr);
            Assert.Equal((IntPtr)1, result);
            Assert.False(fired);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void HookCallback_EscapeKeyDown_WhenNotCancellable_DoesNotSuppress()
    {
        var hook = new KeyboardHook(0x70);
        // IsCancellable is false by default
        var fired = false;
        hook.Cancelled += () => fired = true;

        var ptr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(ptr, KeyboardHook.VK_ESCAPE);
            var result = hook.HookCallback(0, (IntPtr)KeyboardHook.WM_KEYDOWN, ptr);
            Assert.NotEqual((IntPtr)1, result);
            Assert.False(fired);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void HookCallback_NegativeNCode_PassesThrough()
    {
        // nCode < 0 means the hook must call CallNextHookEx and return its value (never suppress)
        var hook = new KeyboardHook(0x70);
        hook.IsCancellable = true;
        var fired = false;
        hook.Cancelled += () => fired = true;

        var ptr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(ptr, KeyboardHook.VK_ESCAPE);
            var result = hook.HookCallback(-1, (IntPtr)KeyboardHook.WM_KEYDOWN, ptr);
            Assert.NotEqual((IntPtr)1, result);
            Assert.False(fired);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void HookCallback_HotkeyKeyDown_FiresKeyDownAndSuppresses()
    {
        var hook = new KeyboardHook(0x70 /* F1 */);
        var fired = false;
        hook.KeyDown += () => fired = true;

        var ptr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(ptr, 0x70);
            var result = hook.HookCallback(0, (IntPtr)KeyboardHook.WM_KEYDOWN, ptr);
            Assert.Equal((IntPtr)1, result);
            Assert.True(fired);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void HookCallback_HotkeyKeyUp_FiresKeyUpAndSuppresses()
    {
        var hook = new KeyboardHook(0x70 /* F1 */);
        var fired = false;
        hook.KeyUp += () => fired = true;

        var ptr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(ptr, 0x70);
            hook.HookCallback(0, (IntPtr)KeyboardHook.WM_KEYDOWN, ptr); // prime _recording = true
            var result = hook.HookCallback(0, (IntPtr)KeyboardHook.WM_KEYUP, ptr);
            Assert.Equal((IntPtr)1, result);
            Assert.True(fired);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void HookCallback_UnrelatedKey_PassesThrough()
    {
        var hook = new KeyboardHook(0x70 /* F1 */);
        var keyDownFired = false;
        var keyUpFired = false;
        var cancelledFired = false;
        hook.KeyDown += () => keyDownFired = true;
        hook.KeyUp += () => keyUpFired = true;
        hook.Cancelled += () => cancelledFired = true;

        var ptr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(ptr, 0x41); // 'A' — unrelated key
            var result = hook.HookCallback(0, (IntPtr)KeyboardHook.WM_KEYDOWN, ptr);
            Assert.NotEqual((IntPtr)1, result);
            Assert.False(keyDownFired);
            Assert.False(keyUpFired);
            Assert.False(cancelledFired);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
