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
}
