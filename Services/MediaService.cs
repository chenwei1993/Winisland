using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WinIsland.Services;

public sealed class MediaSnapshot
{
    public bool HasSession { get; init; }
    public bool IsPlaying { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public string App { get; init; } = "媒体";
    public ImageSource? Art { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public TimeSpan CurrentPosition
    {
        get
        {
            var position = Position;
            if (IsPlaying)
            {
                position += DateTime.Now - CapturedAt;
            }

            if (Duration > TimeSpan.Zero && position > Duration)
            {
                position = Duration;
            }

            return position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }
    }
}

public sealed class MediaService : IDisposable
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaSnapshot _current = new();
    private string _artKey = string.Empty;
    private bool _refreshing;
    private bool _pending;
    private bool _started;

    public MediaSnapshot Current => _current;
    public bool IsAvailable => _manager is not null;

    /// <summary>Raised when metadata / playback state changed (not every tick).</summary>
    public event Action? Updated;

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => DispatchRefresh();
            _poll.Tick += (_, _) => _ = RefreshAsync();
            _poll.Start();
            await RefreshAsync();
        }
        catch
        {
            _manager = null;
            _poll.Start();
        }
    }

    private void DispatchRefresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            _ = RefreshAsync();
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
        }
    }

    public async Task RefreshAsync()
    {
        if (_manager is null)
        {
            return;
        }

        if (_refreshing)
        {
            _pending = true;
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _pending = false;
                await RefreshCoreAsync();
            }
            while (_pending);
        }
        catch
        {
            // Media apps come and go; ignore transient failures.
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        var manager = _manager;
        if (manager is null)
        {
            return;
        }

        GlobalSystemMediaTransportControlsSession? session = manager.GetCurrentSession();

        if (session is null)
        {
            session = manager.GetSessions().FirstOrDefault(candidate =>
            {
                try
                {
                    return candidate.GetPlaybackInfo()?.PlaybackStatus ==
                           GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                }
                catch
                {
                    return false;
                }
            });
        }

        if (session is null)
        {
            _session = null;
            if (_current.HasSession)
            {
                _artKey = string.Empty;
                SetSnapshot(new MediaSnapshot());
            }

            return;
        }

        if (!ReferenceEquals(session, _session))
        {
            _session = session;
            _artKey = string.Empty;
            session.MediaPropertiesChanged += (_, _) => DispatchRefresh();
            session.PlaybackInfoChanged += (_, _) => DispatchRefresh();
            session.TimelinePropertiesChanged += (_, _) => DispatchRefresh();
        }

        GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback = null;
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = null;
        GlobalSystemMediaTransportControlsSessionMediaProperties? properties = null;

        try
        {
            playback = session.GetPlaybackInfo();
            timeline = session.GetTimelineProperties();
            properties = await session.TryGetMediaPropertiesAsync();
        }
        catch
        {
            // Keep whatever we had.
        }

        var playing = playback?.PlaybackStatus ==
                      GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var title = properties?.Title ?? string.Empty;
        var artist = properties?.Artist ?? string.Empty;
        var album = properties?.AlbumTitle ?? string.Empty;
        var app = FriendlyAppName(session.SourceAppUserModelId);

        var duration = TimeSpan.Zero;
        var position = TimeSpan.Zero;
        if (timeline is not null)
        {
            duration = timeline.EndTime - timeline.StartTime;
            position = timeline.Position - timeline.StartTime;
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            if (duration > TimeSpan.Zero && position > duration)
            {
                position = duration;
            }
        }

        var artKey = $"{title}\u001f{artist}\u001f{album}\u001f{app}";
        var art = _current.Art;
        if (!string.Equals(artKey, _artKey, StringComparison.Ordinal) || art is null)
        {
            _artKey = artKey;
            art = await LoadArtAsync(properties?.Thumbnail);
        }

        var next = new MediaSnapshot
        {
            HasSession = true,
            IsPlaying = playing,
            Title = string.IsNullOrWhiteSpace(title) ? "未知曲目" : title,
            Artist = artist,
            Album = album,
            App = app,
            Art = art,
            Position = position,
            Duration = duration,
            CapturedAt = DateTime.Now
        };

        var previous = _current;
        var changed = !previous.HasSession ||
                      previous.IsPlaying != next.IsPlaying ||
                      previous.Title != next.Title ||
                      previous.Artist != next.Artist ||
                      previous.App != next.App ||
                      !ReferenceEquals(previous.Art, next.Art) ||
                      Math.Abs((previous.Duration - next.Duration).TotalSeconds) > 0.5 ||
                      Math.Abs((previous.Position - next.Position).TotalSeconds) > 2.5;

        SetSnapshot(next);
        if (changed)
        {
            Updated?.Invoke();
        }
    }

    private void SetSnapshot(MediaSnapshot snapshot) => _current = snapshot;

    private static async Task<ImageSource?> LoadArtAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync();
            var size = stream.Size;
            if (size == 0 || size > 16 * 1024 * 1024)
            {
                return null;
            }

            var bytes = new byte[(int)size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(bytes);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 224;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string FriendlyAppName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return "媒体";
        }

        var id = appUserModelId;
        var bang = id.IndexOf('!');
        if (bang >= 0 && bang < id.Length - 1)
        {
            id = id[(bang + 1)..];
        }

        var name = id.Split('\\').Last();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        var lower = name.ToLowerInvariant();
        if (lower.Contains("spotify"))
        {
            return "Spotify";
        }

        if (lower.Contains("zunemusic") || lower.Contains("mediaplayer"))
        {
            return "Media Player";
        }

        if (lower.Contains("chrome"))
        {
            return "Chrome";
        }

        if (lower.Contains("msedge") || lower.Contains("edge"))
        {
            return "Edge";
        }

        if (lower.Contains("firefox"))
        {
            return "Firefox";
        }

        if (lower.Contains("vlc"))
        {
            return "VLC";
        }

        if (lower.Contains("foobar"))
        {
            return "foobar2000";
        }

        if (lower.Contains("qqmusic"))
        {
            return "QQ音乐";
        }

        if (lower.Contains("cloudmusic"))
        {
            return "网易云音乐";
        }

        if (lower.Contains("kugou"))
        {
            return "酷狗音乐";
        }

        if (lower.Contains("potplayer"))
        {
            return "PotPlayer";
        }

        if (lower.Contains("music"))
        {
            return "音乐";
        }

        return string.IsNullOrWhiteSpace(name) ? "媒体" : name;
    }

    public async Task TogglePlayPauseAsync()
    {
        try
        {
            if (_session is not null)
            {
                var result = await _session.TryTogglePlayPauseAsync();
                Trace($"TogglePlayPause result={result}");
            }
            else
            {
                Trace("TogglePlayPause: no session");
            }
        }
        catch (Exception exception)
        {
            Trace($"TogglePlayPause exception: {exception.Message}");
        }

        await RefreshAsync();
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
                $"[{DateTime.Now:HH:mm:ss.fff}] [media] {message}{Environment.NewLine}");
        }
        catch
        {
            // Debug tracing only.
        }
    }

    public async Task NextAsync()
    {
        try
        {
            if (_session is not null)
            {
                await _session.TrySkipNextAsync();
            }
        }
        catch
        {
            // Ignore.
        }

        await RefreshAsync();
    }

    public async Task PreviousAsync()
    {
        try
        {
            if (_session is not null)
            {
                await _session.TrySkipPreviousAsync();
            }
        }
        catch
        {
            // Ignore.
        }

        await RefreshAsync();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        try
        {
            if (_session is null)
            {
                return;
            }

            var timeline = _session.GetTimelineProperties();
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            var duration = timeline.EndTime - timeline.StartTime;
            if (duration > TimeSpan.Zero && position > duration)
            {
                position = duration;
            }

            await _session.TryChangePlaybackPositionAsync((timeline.StartTime + position).Ticks);
        }
        catch
        {
            // Ignore.
        }

        await RefreshAsync();
    }

    public void Dispose()
    {
        _poll.Stop();
        _manager = null;
        _session = null;
    }
}
