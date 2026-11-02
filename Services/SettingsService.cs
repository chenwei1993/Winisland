using System.IO;
using System.Text.Json;

namespace WinIsland.Services;

public sealed class AppSettings
{
    public bool RecordClipboard { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool PlaySound { get; set; }
    public bool ShowSystemNotifications { get; set; } = true;
    public bool EdgeAutoHide { get; set; } = true;
    public bool ShowAppSwitches { get; set; }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinIsland");

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // Settings are best effort only.
        }
    }
}
