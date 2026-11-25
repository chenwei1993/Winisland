using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinIsland.Infrastructure;

namespace WinIsland.Models;

public enum ClipboardKind
{
    Text,
    Image,
    Files
}

public sealed class ClipboardItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public ClipboardKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public BitmapSource? Thumbnail { get; init; }
    public byte[]? ImageBytes { get; init; }
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    public string KindGlyph => Kind switch
    {
        ClipboardKind.Image => "\uE8B9",
        ClipboardKind.Files => "\uE8B7",
        _ => "\uE8C8"
    };

    public string KindText => Kind switch
    {
        ClipboardKind.Image => "图片",
        ClipboardKind.Files => "文件",
        _ => "文本"
    };

    public string MetaText
    {
        get
        {
            var time = CreatedAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            var extra = Kind switch
            {
                ClipboardKind.Text => $"{Text.Length} 字符",
                ClipboardKind.Files => $"{Files.Count} 个项目",
                _ => "截图 / 位图"
            };
            return $"{KindText} · {time} · {extra}";
        }
    }

    public string Tooltip => Kind switch
    {
        ClipboardKind.Files => string.Join(Environment.NewLine, Files),
        ClipboardKind.Image => "点击复制图片",
        _ => Text
    };
}

public sealed class BudsEqualizerOption : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class BudsOptionChoice : ObservableObject
{
    public required string RowId { get; init; }
    public required int Value { get; init; }
    public required string Name { get; init; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class BudsOptionRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool IsRange { get; init; }
    public int Minimum { get; init; }
    public int Maximum { get; init; }

    private int _value;

    public int Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
            {
                OnPropertyChanged(nameof(ValueText));
            }
        }
    }

    public string ValueText => IsRange ? _value.ToString() : string.Empty;

    public ObservableCollection<BudsOptionChoice> Choices { get; } = new();

    public bool HasChoices => Choices.Count > 0;
}

public sealed class BudsDeviceOption
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsConnected { get; init; }
    public bool IsActive { get; init; }
    public bool IsPreferred { get; init; }
    public string CheckGlyph => IsActive ? "\uE73E" : string.Empty;
    public string StateText => IsActive
        ? IsPreferred ? "当前 · 首选" : "当前"
        : IsPreferred ? "首选" : IsConnected ? "已连接" : "未连接";
}

public sealed class AudioOutputDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public string Glyph { get; init; } = "\uE7F4";
    public string CheckGlyph => IsDefault ? "\uE73E" : string.Empty;
}

public sealed class ShelfItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required string Glyph { get; init; }
    public DateTime AddedAt { get; init; } = DateTime.Now;
    public BitmapSource? Thumbnail { get; init; }
    public bool HasThumbnail => Thumbnail is not null;

    public static ShelfItem FromPath(string path)
    {
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return new ShelfItem
            {
                Path = directory.FullName,
                Name = directory.Name,
                SizeText = "文件夹",
                Glyph = "\uE8B7"
            };
        }

        var info = new FileInfo(path);
        var ext = info.Extension.ToLowerInvariant();
        var isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico";

        BitmapSource? thumbnail = null;
        if (isImage && info.Exists && info.Length is > 0 and < 24 * 1024 * 1024)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 96;
                image.UriSource = new Uri(info.FullName);
                image.EndInit();
                image.Freeze();
                thumbnail = image;
            }
            catch
            {
                thumbnail = null;
            }
        }

        return new ShelfItem
        {
            Path = info.FullName,
            Name = info.Name,
            SizeText = FormatSize(info.Exists ? info.Length : 0),
            Glyph = isImage ? "\uE8B9" : "\uE8A5",
            Thumbnail = thumbnail
        };
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}

public sealed class IslandAlert
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; init; } = "通知";
    public string Message { get; init; } = string.Empty;
    public string App { get; init; } = "WinIsland";
    public string Kind { get; init; } = "info";
    public DateTime Time { get; init; } = DateTime.Now;
    public bool IsRead { get; set; }
    public ImageSource? Icon { get; init; }

    public string Glyph => Kind switch
    {
        "success" => "\uE930",
        "warning" => "\uE7BA",
        "error" => "\uE783",
        "timer" => "\uE823",
        "reminder" => "\uEA8F",
        "system" => "\uEA8F",
        _ => "\uE946"
    };

    public string MetaText => $"{App} · {Time:HH:mm}";

    public string Tooltip => $"{Title}\n{Message}";
}

public enum ReminderRepeat
{
    None,
    Daily,
    Weekdays,
    Weekly
}

/// <summary>A scheduled reminder that stays on the island until dismissed.</summary>
public sealed class ReminderItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "提醒";

    private DateTime _targetTime;

    public DateTime TargetTime
    {
        get => _targetTime;
        set
        {
            if (Set(ref _targetTime, value))
            {
                OnPropertyChanged(nameof(TargetText));
                OnPropertyChanged(nameof(ScheduleText));
            }
        }
    }

    private ReminderRepeat _repeat;

    public ReminderRepeat Repeat
    {
        get => _repeat;
        set
        {
            if (Set(ref _repeat, value))
            {
                OnPropertyChanged(nameof(RepeatText));
                OnPropertyChanged(nameof(ScheduleText));
            }
        }
    }

    private bool _isFired;

    public bool IsFired
    {
        get => _isFired;
        set
        {
            if (Set(ref _isFired, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ActionText));
            }
        }
    }

    [JsonIgnore]
    public string TargetText
    {
        get
        {
            var date = TargetTime.Date;
            var today = DateTime.Today;
            if (date == today)
            {
                return $"今天 {TargetTime:HH:mm}";
            }

            if (date == today.AddDays(1))
            {
                return $"明天 {TargetTime:HH:mm}";
            }

            return $"{TargetTime:M月d日 HH:mm}";
        }
    }

    [JsonIgnore]
    public string RepeatText => Repeat switch
    {
        ReminderRepeat.Daily => "每天",
        ReminderRepeat.Weekdays => "工作日",
        ReminderRepeat.Weekly => "每周",
        _ => string.Empty
    };

    [JsonIgnore]
    public string ScheduleText => string.IsNullOrEmpty(RepeatText)
        ? TargetText
        : $"{TargetText} · {RepeatText}";

    [JsonIgnore]
    public string StatusText => IsFired ? "已提醒" : "等待中";

    [JsonIgnore]
    public string ActionText => IsFired ? "关闭" : "取消";
}

/// <summary>A system notification mirrored from the Windows notification center.</summary>
public sealed class SystemNotificationInfo
{
    public string AppName { get; init; } = "Windows 通知";
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public ImageSource? Icon { get; init; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
}

/// <summary>An app that was opened or brought to the foreground.</summary>
public sealed class AppSwitchInfo
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public bool IsNewlyOpened { get; init; }
    public ImageSource? Icon { get; init; }
}

public enum IslandCommandAction
{
    Notify,
    Show,
    Collapse,
    Timer,
    Shelf,
    Progress,
    Usage
}

public sealed class IslandCommand
{
    public IslandCommandAction Action { get; init; } = IslandCommandAction.Notify;
    public string Title { get; init; } = "通知";
    public string Message { get; init; } = string.Empty;
    public string App { get; init; } = "外部应用";
    public string Kind { get; init; } = "info";
    public double Minutes { get; init; } = 5;
    public double Seconds { get; init; } = 5;
    public double Progress { get; init; }
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    // Usage push (action = "usage")
    public string Provider { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string Label { get; init; } = "用量";
    public double Used { get; init; }
    public double Limit { get; init; }
    public double? UsedPercent { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public double Balance { get; init; }
    public string Currency { get; init; } = string.Empty;

    public static IslandCommand? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        line = line.Trim();
        if (line.StartsWith('{'))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                var root = document.RootElement;

                var actionText = ReadString(root, "action") ?? "notify";
                var action = actionText.ToLowerInvariant() switch
                {
                    "show" => IslandCommandAction.Show,
                    "collapse" => IslandCommandAction.Collapse,
                    "timer" => IslandCommandAction.Timer,
                    "shelf" => IslandCommandAction.Shelf,
                    "progress" => IslandCommandAction.Progress,
                    "usage" => IslandCommandAction.Usage,
                    _ => IslandCommandAction.Notify
                };

                var progress = ReadNumber(root, "progress") ?? 0;
                if (progress > 1)
                {
                    progress /= 100;
                }

                var used = ReadNumber(root, "used") ?? 0;
                var limit = ReadNumber(root, "limit") ?? 0;
                double? usedPercent = null;
                if (limit > 0)
                {
                    usedPercent = Math.Clamp(used / limit * 100d, 0, 100);
                }
                else if (ReadNumber(root, "percent") is { } percent)
                {
                    usedPercent = Math.Clamp(percent <= 1 ? percent * 100d : percent, 0, 100);
                }

                return new IslandCommand
                {
                    Action = action,
                    Title = ReadString(root, "title") ?? "通知",
                    Message = ReadString(root, "message") ?? string.Empty,
                    App = ReadString(root, "app") ?? "外部应用",
                    Kind = (ReadString(root, "kind") ?? "info").ToLowerInvariant(),
                    Minutes = ReadNumber(root, "minutes") ?? 5,
                    Seconds = ReadNumber(root, "seconds") ?? 5,
                    Progress = Math.Clamp(progress, 0, 1),
                    Files = ReadStringArray(root, "files"),
                    Provider = ReadString(root, "provider") ?? string.Empty,
                    ProviderName = ReadString(root, "providerName") ?? ReadString(root, "name") ?? string.Empty,
                    Label = ReadString(root, "label") ?? "用量",
                    Used = used,
                    Limit = limit,
                    UsedPercent = usedPercent,
                    ResetAt = ReadTimestamp(ReadString(root, "resetAt")),
                    Balance = ReadNumber(root, "balance") ?? 0,
                    Currency = ReadString(root, "currency") ?? string.Empty
                };
            }
            catch
            {
                // Fall through to plain text parsing.
            }
        }

        var parts = line.Split('|', 2);
        return new IslandCommand
        {
            Title = parts[0].Trim(),
            Message = parts.Length > 1 ? parts[1].Trim() : string.Empty
        };
    }

    private static string? ReadString(System.Text.Json.JsonElement root, string name)
    {
        foreach (var key in new[] { name, char.ToUpperInvariant(name[0]) + name[1..] })
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static double? ReadNumber(System.Text.Json.JsonElement root, string name)
    {
        foreach (var key in new[] { name, char.ToUpperInvariant(name[0]) + name[1..] })
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                return value.GetDouble();
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadTimestamp(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(System.Text.Json.JsonElement root, string name)
    {
        foreach (var key in new[] { name, char.ToUpperInvariant(name[0]) + name[1..] })
        {
            if (!root.TryGetProperty(key, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                continue;
            }

            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var path = item.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        result.Add(path);
                    }
                }
            }

            return result;
        }

        return Array.Empty<string>();
    }
}

public sealed class CalendarDay
{
    public required DateTime Date { get; init; }
    public required int Day { get; init; }
    public required bool IsCurrentMonth { get; init; }
    public required bool IsToday { get; init; }
}

public enum ActivityKind
{
    Media,
    Timer,
    Volume,
    Brightness,
    Battery,
    Clipboard,
    Alert,
    App,
    Info,
    Update
}

/// <summary>Everything the compact island can display.</summary>
public sealed class LiveActivity
{
    public ActivityKind Kind { get; init; }
    public string Glyph { get; init; } = "\uE946";
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public ImageSource? Art { get; init; }
    public double Progress { get; init; }
    public bool ShowProgress { get; init; }
    public bool ShowRing { get; init; }
    public bool ShowMediaControls { get; init; }
    public bool IsPlaying { get; init; }
    /// <summary>Shows the sticky "关闭提醒" button on the compact face.</summary>
    public bool ShowDismissButton { get; init; }
    public Brush? Accent { get; init; }
}
