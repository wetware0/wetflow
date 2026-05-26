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

    public static (AppSettings settings, Exception? error) Load() => Load(SettingsPath);

    internal static (AppSettings settings, Exception? error) Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return (JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings(), null);
            }
        }
        catch (Exception ex)
        {
            TrayApp.LogError(ex);
            return (new AppSettings(), ex);
        }
        return (new AppSettings(), null);
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
