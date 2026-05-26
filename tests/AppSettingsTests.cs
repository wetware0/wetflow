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
        Assert.Equal(0.5, settings.ShortPauseSecs);
        Assert.Equal(1.5, settings.LongPauseSecs);
        Assert.True(settings.UseToggleMode);
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

    [Fact]
    public void AppSettings_RoundTrip_PreservesPauseThresholds()
    {
        var original = new AppSettings { ShortPauseSecs = 0.3, LongPauseSecs = 2.0 };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(0.3, loaded.ShortPauseSecs);
        Assert.Equal(2.0, loaded.LongPauseSecs);
    }

    [Fact]
    public void AppSettings_RoundTrip_PreservesToggleMode()
    {
        var original = new AppSettings { UseToggleMode = true };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.True(loaded.UseToggleMode);
    }

    [Fact]
    public void AppSettings_RoundTrip_PreservesToggleModeWhenFalse()
    {
        var original = new AppSettings { UseToggleMode = false };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.False(loaded.UseToggleMode);
    }

    [Fact]
    public void AppSettings_Deserialize_ThrowsOnInvalidJson()
    {
        // Exercises the exception type that Load() catches. Load() itself is not tested
        // directly here to avoid filesystem coupling — see AppSettings.Load() catch block.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AppSettings>("{ not valid json"));
    }
}
