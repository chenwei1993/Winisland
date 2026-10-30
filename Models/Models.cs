using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        "system" => "\uEA8F",
        _ => "\uE946"
    };

    public string MetaText => $"{App} · {Time:HH:mm}";

    public string Tooltip => $"{Title}\n{Message}";
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
    Progress
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
                    _ => IslandCommandAction.Notify
                };

                var progress = ReadNumber(root, "progress") ?? 0;
                if (progress > 1)
                {
                    progress /= 100;
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
                    Files = ReadStringArray(root, "files")
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
    Info
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
    public Brush? Accent { get; init; }
}
