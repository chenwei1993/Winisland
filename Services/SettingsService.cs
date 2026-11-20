using System.IO;
using System.Text.Json;

namespace WinIsland.Services;

public enum UsageIslandMode
{
    /// <summary>Only show the usage chip when a window crosses the warning threshold.</summary>
    AlertsOnly,
    Always,
    Off
}

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

    // ── AI usage ────────────────────────────────────────────────────────
    public bool UsageEnabled { get; set; } = true;
    public UsageIslandMode UsageIslandMode { get; set; } = UsageIslandMode.AlertsOnly;
    public int UsageWarnPercent { get; set; } = 80;
    public decimal DeepSeekBudget { get; set; }
    public decimal DeepSeekLowBalance { get; set; } = 5m;
    public Dictionary<string, bool> UsageProviders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provider ids in the order the user arranged them.</summary>
    public List<string> UsageProviderOrder { get; set; } = new();

    /// <summary>How often enabled providers are polled, in minutes.</summary>
    public int UsageRefreshMinutes { get; set; } = 2;

    // Legacy per-provider flags kept only so settings written by 1.1.0 migrate cleanly.
    public bool DeepSeekEnabled { get; set; } = true;
    public bool OpenCodeGoEnabled { get; set; } = true;
}

public sealed class SettingsService
{
    private const int CurrentVersion = 5;

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
                var migrated = false;

                if (Current.Version < 2)
                {
                    // v1 only had "always hidden in edge mode". Preserve the
                    // always-visible choice, and turn the old edge mode into the
                    // new smart-avoid mode so it stops covering window chrome.
                    Current.SmartAvoidWindows = Current.EdgeAutoHide;
                    Current.EdgeAutoHide = false;
                }

                if (Current.Version < 3)
                {
                    Current.Version = 3;
                    migrated = true;
                }

                if (Current.Version < 4)
                {
                    // v3 had one boolean per provider; v4 keeps a dictionary so
                    // new providers need no settings-schema change.
                    if (!Current.DeepSeekEnabled)
                    {
                        Current.UsageProviders["deepseek"] = false;
                    }

                    if (!Current.OpenCodeGoEnabled)
                    {
                        Current.UsageProviders["opencode-go"] = false;
                    }

                    Current.Version = 4;
                    migrated = true;
                }

                if (Current.Version < 5)
                {
                    // v5 adds a user-arranged provider order and a refresh interval.
                    if (Current.UsageRefreshMinutes <= 0)
                    {
                        Current.UsageRefreshMinutes = 2;
                    }

                    Current.Version = 5;
                    migrated = true;
                }

                if (migrated)
                {
                    Save();
                }
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
