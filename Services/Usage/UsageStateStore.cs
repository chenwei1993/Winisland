using System.IO;
using System.Text.Json;

namespace WinIsland.Services.Usage;

/// <summary>
/// Small persisted state: which threshold alerts already fired (per window and
/// reset time) and the balance delta used to estimate today's spend.
/// </summary>
public sealed class UsageStateStore
{
    private sealed class StateFile
    {
        public List<string> Alerted { get; set; } = new();
        public string SpendDate { get; set; } = string.Empty;
        public decimal SpendAmount { get; set; }
        public decimal LastBalance { get; set; }
        public string LastCurrency { get; set; } = string.Empty;
    }

    private readonly string _path;
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);

    public UsageStateStore(string directory)
    {
        _path = Path.Combine(directory, "usage-state.json");
    }

    public string SpendDate { get; set; } = string.Empty;

    public decimal SpendAmount { get; set; }

    public decimal LastBalance { get; set; }

    public string LastCurrency { get; set; } = string.Empty;

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_path));
            if (file is null)
            {
                return;
            }

            _alerted.Clear();
            foreach (var key in file.Alerted)
            {
                _alerted.Add(key);
            }

            SpendDate = file.SpendDate;
            SpendAmount = file.SpendAmount;
            LastBalance = file.LastBalance;
            LastCurrency = file.LastCurrency;
        }
        catch
        {
            // A corrupt state file only means alerts may fire once more.
        }
    }

    public bool MarkAlerted(string key)
    {
        if (!_alerted.Add(key))
        {
            return false;
        }

        if (_alerted.Count > 256)
        {
            // Keep the set bounded; a dropped key can only cause one extra alert.
            _alerted.Clear();
            _alerted.Add(key);
        }

        Save();
        return true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var file = new StateFile
            {
                Alerted = _alerted.ToList(),
                SpendDate = SpendDate,
                SpendAmount = SpendAmount,
                LastBalance = LastBalance,
                LastCurrency = LastCurrency
            };

            File.WriteAllText(_path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }
}
