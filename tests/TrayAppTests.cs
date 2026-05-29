using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class TrayAppTests
{
    [Theory]
    [InlineData("hello", false, null, false)]             // success → delete
    [InlineData("", false, null, true)]                   // empty transcript → preserve
    [InlineData("  ", false, null, true)]                 // whitespace-only → preserve
    [InlineData(null, true, null, false)]                 // cancelled → delete
    [InlineData("hello", false, typeof(IOException), true)] // exception → preserve
    public void ShouldPreserveAudio_ReturnsExpected(
        string? text, bool wasCancelled, Type? exType, bool expected)
    {
        var ex = exType != null ? (Exception)Activator.CreateInstance(exType)! : null;
        Assert.Equal(expected, TrayApp.ShouldPreserveAudio(text, wasCancelled, ex));
    }
}
