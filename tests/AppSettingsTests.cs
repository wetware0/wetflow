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
        Assert.False(settings.UseGpu);
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
    public void AppSettings_RoundTrip_PreservesUseGpuWhenTrue()
    {
        var original = new AppSettings { UseGpu = true };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.True(loaded.UseGpu);
    }

    [Fact]
    public void AppSettings_RoundTrip_PreservesUseGpuWhenFalse()
    {
        var original = new AppSettings { UseGpu = false };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.False(loaded.UseGpu);
    }

    [Fact]
    public void AppSettings_Deserialize_ThrowsOnInvalidJson()
    {
        // Exercises the exception type that Load() catches. Load() itself is not tested
        // directly here to avoid filesystem coupling — see AppSettings.Load() catch block.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AppSettings>("{ not valid json"));
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileDoesNotExist()
    {
        var (settings, error) = AppSettings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));

        Assert.Null(error);
        Assert.Equal(-1, settings.OverlayX);
    }

    [Fact]
    public void Load_ReturnsError_WhenFileIsCorrupt()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ not valid json");
            var (settings, error) = AppSettings.Load(path);

            Assert.NotNull(error);
            Assert.IsType<JsonException>(error);
            Assert.Equal(-1, settings.OverlayX); // fallback defaults
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ReturnsSettings_WhenFileIsValid()
    {
        var path = Path.GetTempFileName();
        try
        {
            var original = new AppSettings { HotkeyVKey = 0x70, WhisperModel = "small", OverlayX = 100, OverlayY = 200 };
            File.WriteAllText(path, JsonSerializer.Serialize(original));

            var (loaded, error) = AppSettings.Load(path);

            Assert.Null(error);
            Assert.Equal(0x70, loaded.HotkeyVKey);
            Assert.Equal("small", loaded.WhisperModel);
            Assert.Equal(100, loaded.OverlayX);
            Assert.Equal(200, loaded.OverlayY);
        }
        finally { File.Delete(path); }
    }
}
