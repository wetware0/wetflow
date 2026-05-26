using System.Text.Json;
using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class AppSettingsTests
{
    [Fact]
    public void AppSettings_RoundTrip_PreservesOverlayPosition()
    {
        var original = new AppSettings { OverlayX = 123, OverlayY = 456, ShowOverlay = false };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(123, loaded.OverlayX);
        Assert.Equal(456, loaded.OverlayY);
        Assert.False(loaded.ShowOverlay);
    }

    [Fact]
    public void AppSettings_Defaults_HaveUnsetSentinel()
    {
        var settings = new AppSettings();
        Assert.Equal(-1, settings.OverlayX);
        Assert.Equal(-1, settings.OverlayY);
        Assert.True(settings.ShowOverlay);
    }

    [Fact]
    public void AppSettings_RoundTrip_PreservesHotkeyAndModel()
    {
        var original = new AppSettings { HotkeyVKey = 0x71, WhisperModel = "small" };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(0x71, loaded.HotkeyVKey);
        Assert.Equal("small", loaded.WhisperModel);
    }
}
