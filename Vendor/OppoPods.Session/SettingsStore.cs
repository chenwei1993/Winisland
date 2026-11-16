using System.Text.Json;

namespace OppoPods.Session;

/// <summary>偏好设置持久化(%APPDATA%\OppoPods\settings.json,对齐 macOS 版 UserDefaults 仅有的两键)。</summary>
public sealed class SettingsStore
{
    private sealed record SettingsData(string? SelectedDeviceAddress, bool AutoConnect);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OppoPods", "settings.json");

    private readonly object _gate = new();

    public string LoadSelectedDeviceAddress()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return string.Empty;
                var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath));
                return data?.SelectedDeviceAddress ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public bool LoadAutoConnect()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return true;
                var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath));
                return data?.AutoConnect ?? true;
            }
            catch
            {
                return true;
            }
        }
    }

    public void Save(string selectedDeviceAddress, bool autoConnect)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var json = JsonSerializer.Serialize(new SettingsData(selectedDeviceAddress, autoConnect));
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // 持久化失败不影响运行
            }
        }
    }
}
