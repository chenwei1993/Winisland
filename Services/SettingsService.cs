using System.IO;
using System.Text.Json;

namespace WinIsland.Services;

public sealed class AppSettings
{
    public int Version { get; set; }
    public bool RecordClipboard { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool PlaySound { get; set; }
    public bool ShowSystemNotifications { get; set; } = true;
    public bool EdgeAutoHide { get; set; }
    public bool SmartAvoidWindows { get; set; }
    public bool ShowAppSwitches { get; set; }
}

public sealed class SettingsService
{
    private const int CurrentVersion = 2;

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
            if (!File.Exists(SettingsPath))
            {
                Current = CreateDefault();
                return;
            }

            var json = File.ReadAllText(SettingsPath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? CreateDefault();

            if (Current.Version < CurrentVersion)
            {
                // v1 only had "always hidden in edge mode". Preserve the
                // always-visible choice, and turn the old edge mode into the
                // new smart-avoid mode so it stops covering window chrome.
                Current.SmartAvoidWindows = Current.EdgeAutoHide;
                Current.EdgeAutoHide = false;
                Current.Version = CurrentVersion;
                Save();
            }
        }
        catch
        {
            Current = CreateDefault();
        }
    }

    private static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            Version = CurrentVersion,
            SmartAvoidWindows = true
        };
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
