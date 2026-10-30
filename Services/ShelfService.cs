using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using WinIsland.Models;

namespace WinIsland.Services;

public sealed class ShelfService
{
    private readonly object _gate = new();
    private readonly SettingsService _settings;
    private bool _loading;

    public ObservableCollection<ShelfItem> Items { get; } = new();

    public string ShelfPath => Path.Combine(_settings.DataDirectory, "shelf.json");

    public ShelfService(SettingsService settings)
    {
        _settings = settings;
        Items.CollectionChanged += (_, _) =>
        {
            if (!_loading)
            {
                Save();
            }
        };
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(ShelfPath))
            {
                return;
            }

            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ShelfPath)) ?? new List<string>();
            _loading = true;
            foreach (var path in paths)
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    Items.Add(ShelfItem.FromPath(path));
                }
            }
        }
        catch
        {
            // Ignore corrupted shelf file.
        }
        finally
        {
            _loading = false;
        }
    }

    public int Add(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(path);
                if (Items.Any(i => string.Equals(i.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Items.Insert(0, ShelfItem.FromPath(fullPath));
                added++;
            }
            catch
            {
                // Skip inaccessible items.
            }
        }

        while (Items.Count > 40)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        if (added > 0)
        {
            Save();
        }

        return added;
    }

    public void Remove(ShelfItem item) => Items.Remove(item);

    public void Clear() => Items.Clear();

    private void Save()
    {
        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(ShelfPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(ShelfPath, JsonSerializer.Serialize(Items.Select(i => i.Path).ToList()));
            }
        }
        catch
        {
            // Best effort persistence.
        }
    }
}
