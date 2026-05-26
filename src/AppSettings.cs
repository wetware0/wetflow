using System.Text.Json;

namespace WetFlow;

public class AppSettings
{
    public int HotkeyVKey { get; set; } = (int)Keys.F12;
    public string WhisperModel { get; set; } = "base";
    public int OverlayX { get; set; } = -1; // -1 = unset; PositionOverlay() falls back to bottom-right corner
    public int OverlayY { get; set; } = -1;
    public bool ShowOverlay { get; set; } = true;
    public double ShortPauseSecs { get; set; } = 0.5;
    public double LongPauseSecs { get; set; } = 1.5;
    public bool UseToggleMode { get; set; } = true;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "wetflow", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            TrayApp.LogError(ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            TrayApp.LogError(ex);
        }
    }
}
