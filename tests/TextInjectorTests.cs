using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class TextInjectorTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task InjectAsync_ClipboardOnly_SetsClipboardText()
    {
        await TextInjector.InjectAsync("hello clipboard", OutputMode.ClipboardOnly);

        var tcs = new TaskCompletionSource<string>();
        var staThread = new Thread(() =>
        {
            try { tcs.SetResult(Clipboard.GetText()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();

        var captured = await tcs.Task;
        Assert.Equal("hello clipboard", captured);
    }
}
