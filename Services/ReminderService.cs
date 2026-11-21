using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using WinIsland.Infrastructure;
using WinIsland.Models;

namespace WinIsland.Services;

/// <summary>
/// Scheduled reminders. A due reminder stays on the island until the user
/// dismisses it; the list survives restarts in <c>reminders.json</c>.
/// </summary>
public sealed class ReminderService : ObservableObject, IDisposable
{
    private sealed class ReminderFile
    {
        public List<ReminderItem> Reminders { get; set; } = new();
    }

    private readonly string _path;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _started;
    private bool _disposed;

    public ReminderService(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "reminders.json");
        Reminders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasReminders));
    }

    public ObservableCollection<ReminderItem> Reminders { get; } = new();

    public bool HasReminders => Reminders.Count > 0;

    public IReadOnlyList<ReminderItem> FiredPending =>
        Reminders.Where(r => r.IsFired).OrderBy(r => r.TargetTime).ToArray();

    /// <summary>Raised once when a scheduled time is reached.</summary>
    public event Action<ReminderItem>? Due;

    /// <summary>Raised when a fired reminder is dismissed from the list.</summary>
    public event Action<Guid>? Dismissed;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        Load();
        _timer.Tick += (_, _) => CheckDue();
        _timer.Start();
        CheckDue();
    }

    public ReminderItem Add(DateTime target, string title)
    {
        var item = new ReminderItem
        {
            Title = string.IsNullOrWhiteSpace(title) ? "提醒" : title.Trim(),
            TargetTime = target
        };

        Reminders.Add(item);
        Sort();
        Save();
        CheckDue();
        return item;
    }

    public ReminderItem AddAfter(TimeSpan delay, string title) =>
        Add(DateTime.Now + delay, title);

    /// <summary>Cancels a pending reminder or clears a fired one from the list.</summary>
    public void Dismiss(Guid id)
    {
        var item = Reminders.FirstOrDefault(r => r.Id == id);
        if (item is null)
        {
            return;
        }

        var wasFired = item.IsFired;
        Reminders.Remove(item);
        Save();

        if (wasFired)
        {
            Dismissed?.Invoke(id);
        }
    }

    private void CheckDue()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTime.Now;
        var due = Reminders
            .Where(r => !r.IsFired && r.TargetTime <= now)
            .OrderBy(r => r.TargetTime)
            .ToArray();

        foreach (var item in due)
        {
            item.IsFired = true;
            Save();
            Due?.Invoke(item);
        }
    }

    private void Sort()
    {
        var ordered = Reminders.OrderBy(r => r.TargetTime).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Reminders.IndexOf(ordered[i]);
            if (current != i)
            {
                Reminders.Move(current, i);
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<ReminderFile>(File.ReadAllText(_path));
            if (file is null)
            {
                return;
            }

            Reminders.Clear();
            foreach (var item in file.Reminders
                         .Where(r => r.Id != Guid.Empty && r.TargetTime > DateTime.MinValue)
                         .OrderBy(r => r.TargetTime))
            {
                Reminders.Add(item);
            }
        }
        catch
        {
            // A corrupt reminder file only drops the schedule.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var file = new ReminderFile { Reminders = Reminders.ToList() };
            File.WriteAllText(_path, JsonSerializer.Serialize(file, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // Best effort; the in-memory schedule still works.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        Save();
    }
}
