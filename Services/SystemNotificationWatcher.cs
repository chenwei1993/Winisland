using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using WinIsland.Models;

namespace WinIsland.Services;

/// <summary>
/// Mirrors notifications from the Windows notification center (WeChat, mail,
/// Windows Security, etc.) into the island.
/// </summary>
public sealed class SystemNotificationWatcher : IDisposable
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.Ordinal);
    private UserNotificationListener? _listener;
    private bool _started;
    private bool _ready;
    private bool _busy;
    private bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public event Action<SystemNotificationInfo>? Received;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _poll.Tick += (_, _) => _ = RefreshAsync(false);
        _poll.Start();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            Trace("access: " + access);

            if (!string.Equals(access.ToString(), "Allowed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                _listener.NotificationChanged += OnNotificationChanged;
            }
            catch
            {
                // The event is optional; polling still works.
            }

            // Mark everything already in the notification center as seen, so
            // starting the island doesn't replay yesterday's notifications.
            await RefreshAsync(true);
            _ready = true;
        }
        catch (Exception exception)
        {
            Trace("initialize failed: " + exception.Message);
        }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            _ = RefreshAsync(false);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync(false)));
        }
    }

    private async Task RefreshAsync(bool baseline)
    {
        if (!_started || _busy || _listener is null || (!baseline && !_ready))
        {
            return;
        }

        _busy = true;
        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var notification in notifications.OrderBy(n => n.CreationTime))
            {
                var key = $"{notification.Id}:{notification.CreationTime.UtcTicks}";
                if (!_seen.Add(key))
                {
                    continue;
                }

                if (baseline)
                {
                    continue;
                }

                if (!_enabled)
                {
                    continue;
                }

                await ProcessAsync(notification);
            }

            CleanupRecent();
        }
        catch (Exception exception)
        {
            Trace("refresh failed: " + exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ProcessAsync(UserNotification notification)
    {
        var appInfo = notification.AppInfo;
        var appName = appInfo?.DisplayInfo?.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = "Windows 通知";
        }

        if (appName.Contains("WinIsland", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var texts = new List<string>();
        try
        {
            foreach (var binding in notification.Notification.Visual.Bindings)
            {
                foreach (var text in binding.GetTextElements())
                {
                    var value = text.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        texts.Add(value);
                    }
                }

                if (texts.Count > 0)
                {
                    break;
                }
            }
        }
        catch
        {
            // Ignore malformed toast payloads.
        }

        if (texts.Count == 0)
        {
            return;
        }

        var title = texts[0];
        var body = texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : string.Empty;
        if (title.Length > 120)
        {
            title = title[..120];
        }

        if (body.Length > 400)
        {
            body = body[..400];
        }

        var recentKey = $"{appName}\u001f{title}\u001f{body}";
        var now = DateTime.Now;
        if (_recent.TryGetValue(recentKey, out var previous) && (now - previous).TotalSeconds < 8)
        {
            return;
        }

        _recent[recentKey] = now;

        var appId = appInfo?.AppUserModelId;
        if (string.IsNullOrWhiteSpace(appId))
        {
            appId = appName;
        }

        var icon = await GetIconAsync(appInfo, appId);
        var info = new SystemNotificationInfo
        {
            AppName = appName,
            Title = title,
            Body = body,
            Icon = icon,
            Time = notification.CreationTime
        };

        Trace($"notification from {appName}: {title} / {body}");
        Received?.Invoke(info);
    }

    private async Task<ImageSource?> GetIconAsync(AppInfo? appInfo, string cacheKey)
    {
        if (appInfo is null)
        {
            return null;
        }

        if (_iconCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            var logo = appInfo.DisplayInfo?.GetLogo(new Windows.Foundation.Size(48, 48));
            if (logo is null)
            {
                _iconCache[cacheKey] = null;
                return null;
            }

            using var stream = await logo.OpenReadAsync();
            var size = stream.Size;
            if (size == 0 || size > 8 * 1024 * 1024)
            {
                _iconCache[cacheKey] = null;
                return null;
            }

            var bytes = new byte[(int)size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(bytes);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            var trimmed = TrimTransparent(image);
            _iconCache[cacheKey] = trimmed;
            return trimmed;
        }
        catch
        {
            _iconCache[cacheKey] = null;
            return null;
        }
    }

    private static BitmapSource TrimTransparent(BitmapSource source)
    {
        try
        {
            BitmapSource bitmap = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            if (width <= 1 || height <= 1)
            {
                return source;
            }

            var stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (pixels[y * stride + x * 4 + 3] <= 8)
                    {
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return source;
            }

            var cropWidth = maxX - minX + 1;
            var cropHeight = maxY - minY + 1;
            if (cropWidth == width && cropHeight == height)
            {
                return source;
            }

            var cropped = new CroppedBitmap(bitmap, new Int32Rect(minX, minY, cropWidth, cropHeight));
            cropped.Freeze();
            return cropped;
        }
        catch
        {
            return source;
        }
    }

    private void CleanupRecent()
    {
        if (_recent.Count < 64)
        {
            return;
        }

        var cutoff = DateTime.Now.AddSeconds(-30);
        foreach (var key in _recent.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToList())
        {
            _recent.Remove(key);
        }
    }

    private static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("WINISLAND_DEBUG") != "1")
        {
            return;
        }

        try
        {
            var directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinIsland");
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(directory, "debug.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [notifications] {message}{Environment.NewLine}");
        }
        catch
        {
            // Debug tracing only.
        }
    }

    public void Dispose()
    {
        _started = false;
        _poll.Stop();

        if (_listener is not null)
        {
            try
            {
                _listener.NotificationChanged -= OnNotificationChanged;
            }
            catch
            {
                // Ignore.
            }

            _listener = null;
        }
    }
}
