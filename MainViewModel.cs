using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WinIsland.Infrastructure;
using WinIsland.Models;
using WinIsland.Services;

namespace WinIsland;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");

    private readonly ServiceHub _s;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private string _timeText = "--:--";
    private string _timeWithSecondsText = "--:--:--";
    private string _dateText = string.Empty;
    private string _fullDateText = string.Empty;
    private string _weekText = string.Empty;
    private string _uptimeText = string.Empty;

    private bool _hasMedia;
    private bool _isPlaying;
    private string _mediaTitle = "暂无正在播放";
    private string _mediaArtist = string.Empty;
    private string _mediaAlbum = string.Empty;
    private string _mediaSource = "WinIsland";
    private ImageSource? _mediaArt;
    private double _mediaPosition;
    private double _mediaDuration = 1;
    private string _positionText = "0:00";
    private string _durationText = "0:00";
    private string _playPauseGlyph = "\uE768";
    private bool _canSeek;

    private double _volumePercent;
    private bool _isMuted;
    private string _muteGlyph = "\uE767";
    private string _outputDeviceName = "播放设备";
    private string _outputDeviceGlyph = "\uE7F5";

    private double _brightnessPercent;
    private bool _isBrightnessAvailable;

    private int _batteryPercent = 100;
    private bool _isCharging;
    private bool _hasBattery = true;
    private string _batteryDisplayText = "100%";
    private string _powerStateText = "已接通电源";
    private string _batteryTimeText = string.Empty;

    private double _cpuPercent;
    private double[] _cpuSamples = Array.Empty<double>();
    private double _ramPercent;
    private string _ramUsedText = string.Empty;
    private double[] _ramSamples = Array.Empty<double>();
    private string _netDownText = "0 B/s";
    private string _netUpText = "0 B/s";
    private double[] _netSamples = Array.Empty<double>();
    private string _diskText = string.Empty;
    private string _adapterText = "网络";

    private bool _micInUse;
    private bool _cameraInUse;
    private bool _capsLockOn;
    private string _micStatusText = "麦克风空闲";
    private string _cameraStatusText = "摄像头空闲";
    private string _capsStatusText = "大写锁定关闭";

    private string _countdownText = "00:00";
    private double _countdownProgress;
    private string _countdownLabel = "计时";
    private string _countdownStatusText = "准备就绪";
    private string _countdownButtonText = "开始";
    private bool _countdownActive;
    private bool _countdownPaused;
    private string _stopwatchText = "00:00.0";
    private string _stopwatchButtonText = "开始";
    private bool _stopwatchRunning;

    private int _unreadCount;

    public MainViewModel(ServiceHub services)
    {
        _s = services;

        _s.Media.Updated += ApplyMedia;
        _s.Audio.Changed += (_, _) => ApplyAudio();
        _s.Audio.DevicesChanged += RefreshOutputDevices;
        _s.Brightness.Changed += _ => ApplyBrightness();
        _s.Power.Updated += ApplyPower;
        _s.Monitor.Updated += ApplyMonitor;
        _s.Sensors.Changed += ApplySensors;
        _s.Timers.Updated += ApplyTimers;
        _s.Clipboard.Items.CollectionChanged += (_, _) => OnClipboardChanged();
        _s.Shelf.Items.CollectionChanged += (_, _) => OnShelfChanged();
        Alerts.CollectionChanged += (_, _) => UpdateAlertMeta();

        _clockTimer.Tick += (_, _) => TickClock();
        _clockTimer.Start();
        _positionTimer.Tick += (_, _) => TickPosition();
        _positionTimer.Start();

        TickClock();
        ApplyMedia();
        ApplyAudio();
        RefreshOutputDevices();
        ApplyBrightness();
        ApplyPower();
        ApplyMonitor();
        ApplySensors();
        ApplyTimers();
        OnClipboardChanged();
        OnShelfChanged();
        UpdateAlertMeta();
    }

    // ── Clock ────────────────────────────────────────────────────────────
    public string TimeText
    {
        get => _timeText;
        private set => Set(ref _timeText, value);
    }

    public string TimeWithSecondsText
    {
        get => _timeWithSecondsText;
        private set => Set(ref _timeWithSecondsText, value);
    }

    public string DateText
    {
        get => _dateText;
        private set => Set(ref _dateText, value);
    }

    public string FullDateText
    {
        get => _fullDateText;
        private set => Set(ref _fullDateText, value);
    }

    public string WeekText
    {
        get => _weekText;
        private set => Set(ref _weekText, value);
    }

    public string UptimeText
    {
        get => _uptimeText;
        private set => Set(ref _uptimeText, value);
    }

    private void TickClock()
    {
        var now = DateTime.Now;
        TimeText = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        TimeWithSecondsText = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        DateText = now.ToString("M月d日 ddd", Chinese);
        FullDateText = now.ToString("yyyy年M月d日 dddd", Chinese);
        WeekText = $"第 {ISOWeek.GetWeekOfYear(now)} 周";
        UptimeText = FormatUptime(_s.Monitor.Uptime);
    }

    // ── Media ────────────────────────────────────────────────────────────
    public bool HasMedia
    {
        get => _hasMedia;
        private set => Set(ref _hasMedia, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => Set(ref _isPlaying, value);
    }

    public string MediaTitle
    {
        get => _mediaTitle;
        private set => Set(ref _mediaTitle, value);
    }

    public string MediaArtist
    {
        get => _mediaArtist;
        private set => Set(ref _mediaArtist, value);
    }

    public string MediaAlbum
    {
        get => _mediaAlbum;
        private set => Set(ref _mediaAlbum, value);
    }

    public string MediaSource
    {
        get => _mediaSource;
        private set => Set(ref _mediaSource, value);
    }

    public ImageSource? MediaArt
    {
        get => _mediaArt;
        private set => Set(ref _mediaArt, value);
    }

    public double MediaPosition
    {
        get => _mediaPosition;
        private set => Set(ref _mediaPosition, Math.Max(0, value));
    }

    public double MediaDuration
    {
        get => _mediaDuration;
        private set => Set(ref _mediaDuration, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => Set(ref _positionText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => Set(ref _durationText, value);
    }

    public string PlayPauseGlyph
    {
        get => _playPauseGlyph;
        private set => Set(ref _playPauseGlyph, value);
    }

    public bool CanSeek
    {
        get => _canSeek;
        private set => Set(ref _canSeek, value);
    }

    /// <summary>Set by the view while the user drags the seek bar.</summary>
    public bool IsSeeking { get; set; }

    private void ApplyMedia()
    {
        var media = _s.Media.Current;
        HasMedia = media.HasSession;
        IsPlaying = media.IsPlaying;
        MediaTitle = media.HasSession
            ? (string.IsNullOrWhiteSpace(media.Title) ? "未知曲目" : media.Title)
            : "暂无正在播放";
        MediaArtist = !media.HasSession
            ? "播放任意媒体后即可上岛"
            : string.IsNullOrWhiteSpace(media.Artist) ? "未知艺术家" : media.Artist;
        MediaAlbum = media.Album;
        MediaSource = media.HasSession ? media.App : "WinIsland";
        MediaArt = media.Art;
        MediaDuration = Math.Max(1, media.Duration.TotalSeconds);
        DurationText = FormatTime(media.Duration);
        PlayPauseGlyph = media.IsPlaying ? "\uE769" : "\uE768";
        CanSeek = media.Duration > TimeSpan.Zero;

        if (!IsSeeking)
        {
            TickPosition();
        }
    }

    private void TickPosition()
    {
        if (!HasMedia || IsSeeking)
        {
            return;
        }

        var position = _s.Media.Current.CurrentPosition;
        MediaPosition = position.TotalSeconds;
        PositionText = FormatTime(position);
    }

    // ── Audio ────────────────────────────────────────────────────────────
    public double VolumePercent
    {
        get => _volumePercent;
        private set => Set(ref _volumePercent, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set => Set(ref _isMuted, value);
    }

    public string MuteGlyph
    {
        get => _muteGlyph;
        private set => Set(ref _muteGlyph, value);
    }

    private void ApplyAudio()
    {
        VolumePercent = Math.Round(_s.Audio.Volume * 100d, 1);
        IsMuted = _s.Audio.IsMuted;
        MuteGlyph = _s.Audio.IsMuted ? "\uE74F" : "\uE767";
    }

    public ObservableCollection<AudioOutputDevice> OutputDevices { get; } = new();

    public string OutputDeviceName
    {
        get => _outputDeviceName;
        private set => Set(ref _outputDeviceName, value);
    }

    public string OutputDeviceGlyph
    {
        get => _outputDeviceGlyph;
        private set => Set(ref _outputDeviceGlyph, value);
    }

    public void RefreshOutputDevices()
    {
        var devices = _s.Audio.GetOutputDevices();
        OutputDevices.Clear();

        foreach (var device in devices)
        {
            OutputDevices.Add(new AudioOutputDevice
            {
                Id = device.Id,
                Name = device.Name,
                IsDefault = device.IsDefault,
                Glyph = DeviceGlyph(device.FormFactor, device.Name)
            });
        }

        var current = devices.FirstOrDefault(d => d.IsDefault);
        OutputDeviceName = current?.Name ?? "未找到播放设备";
        OutputDeviceGlyph = current is null ? "\uE7F5" : DeviceGlyph(current.FormFactor, current.Name);
    }

    private static string DeviceGlyph(int formFactor, string name)
    {
        if (formFactor is 3 or 5 ||
            name.Contains("耳机", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("headphone", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("headset", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE7F6";
        }

        if (formFactor is 8 or 9 ||
            name.Contains("HDMI", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("显示器", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE7F4";
        }

        return "\uE7F5";
    }

    // ── Brightness ───────────────────────────────────────────────────────
    public double BrightnessPercent
    {
        get => _brightnessPercent;
        private set => Set(ref _brightnessPercent, value);
    }

    public bool IsBrightnessAvailable
    {
        get => _isBrightnessAvailable;
        private set => Set(ref _isBrightnessAvailable, value);
    }

    private void ApplyBrightness()
    {
        BrightnessPercent = _s.Brightness.Percent;
        IsBrightnessAvailable = _s.Brightness.IsAvailable;
    }

    // ── Power ────────────────────────────────────────────────────────────
    public int BatteryPercent
    {
        get => _batteryPercent;
        private set => Set(ref _batteryPercent, value);
    }

    public bool IsCharging
    {
        get => _isCharging;
        private set => Set(ref _isCharging, value);
    }

    public bool HasBattery
    {
        get => _hasBattery;
        private set => Set(ref _hasBattery, value);
    }

    public string BatteryDisplayText
    {
        get => _batteryDisplayText;
        private set => Set(ref _batteryDisplayText, value);
    }

    public bool HasIdleStatus => HasBattery || MicInUse || CameraInUse || CapsLockOn;

    public string PowerStateText
    {
        get => _powerStateText;
        private set => Set(ref _powerStateText, value);
    }

    public string BatteryTimeText
    {
        get => _batteryTimeText;
        private set => Set(ref _batteryTimeText, value);
    }

    private void ApplyPower()
    {
        BatteryPercent = _s.Power.Percent;
        IsCharging = _s.Power.IsCharging;
        HasBattery = _s.Power.HasBattery;
        BatteryDisplayText = HasBattery ? $"{BatteryPercent}%" : "AC";
        PowerStateText = _s.Power.StateText;
        BatteryTimeText = _s.Power.TimeText;
        OnPropertyChanged(nameof(HasIdleStatus));
    }

    // ── System monitor ───────────────────────────────────────────────────
    public double CpuPercent
    {
        get => _cpuPercent;
        private set => Set(ref _cpuPercent, value);
    }

    public double[] CpuSamples
    {
        get => _cpuSamples;
        private set => Set(ref _cpuSamples, value);
    }

    public double RamPercent
    {
        get => _ramPercent;
        private set => Set(ref _ramPercent, value);
    }

    public string RamUsedText
    {
        get => _ramUsedText;
        private set => Set(ref _ramUsedText, value);
    }

    public double[] RamSamples
    {
        get => _ramSamples;
        private set => Set(ref _ramSamples, value);
    }

    public string NetDownText
    {
        get => _netDownText;
        private set => Set(ref _netDownText, value);
    }

    public string NetUpText
    {
        get => _netUpText;
        private set => Set(ref _netUpText, value);
    }

    public double[] NetSamples
    {
        get => _netSamples;
        private set => Set(ref _netSamples, value);
    }

    public string DiskText
    {
        get => _diskText;
        private set => Set(ref _diskText, value);
    }

    public string AdapterText
    {
        get => _adapterText;
        private set => Set(ref _adapterText, value);
    }

    private void ApplyMonitor()
    {
        CpuPercent = _s.Monitor.CpuPercent;
        CpuSamples = _s.Monitor.CpuSamples;
        RamPercent = _s.Monitor.RamPercent;
        RamUsedText = _s.Monitor.RamUsedText;
        RamSamples = _s.Monitor.RamSamples;
        NetDownText = FormatSpeed(_s.Monitor.DownBytesPerSecond);
        NetUpText = FormatSpeed(_s.Monitor.UpBytesPerSecond);
        NetSamples = _s.Monitor.NetSamples;
        DiskText = _s.Monitor.DiskText;
        AdapterText = _s.Monitor.AdapterText;
        UptimeText = FormatUptime(_s.Monitor.Uptime);
    }

    // ── Sensors ──────────────────────────────────────────────────────────
    public bool MicInUse
    {
        get => _micInUse;
        private set => Set(ref _micInUse, value);
    }

    public bool CameraInUse
    {
        get => _cameraInUse;
        private set => Set(ref _cameraInUse, value);
    }

    public bool CapsLockOn
    {
        get => _capsLockOn;
        private set => Set(ref _capsLockOn, value);
    }

    public string MicStatusText
    {
        get => _micStatusText;
        private set => Set(ref _micStatusText, value);
    }

    public string CameraStatusText
    {
        get => _cameraStatusText;
        private set => Set(ref _cameraStatusText, value);
    }

    public string CapsStatusText
    {
        get => _capsStatusText;
        private set => Set(ref _capsStatusText, value);
    }

    private void ApplySensors()
    {
        MicInUse = _s.Sensors.MicrophoneInUse;
        CameraInUse = _s.Sensors.CameraInUse;
        CapsLockOn = _s.Sensors.CapsLockOn;
        MicStatusText = MicInUse ? "麦克风使用中" : "麦克风空闲";
        CameraStatusText = CameraInUse ? "摄像头使用中" : "摄像头空闲";
        CapsStatusText = CapsLockOn ? "大写锁定开启" : "大写锁定关闭";
        OnPropertyChanged(nameof(HasIdleStatus));
    }

    // ── Clipboard ────────────────────────────────────────────────────────
    public ObservableCollection<ClipboardItem> ClipboardItems => _s.Clipboard.Items;

    public bool HasClipboard => ClipboardItems.Count > 0;

    public string ClipboardCountText => ClipboardItems.Count == 0 ? "暂无记录" : $"{ClipboardItems.Count} 条记录";

    public string ClipboardToggleText => RecordClipboard ? "暂停记录" : "继续记录";

    private void OnClipboardChanged()
    {
        OnPropertyChanged(nameof(HasClipboard));
        OnPropertyChanged(nameof(ClipboardCountText));
    }

    // ── Shelf ────────────────────────────────────────────────────────────
    public ObservableCollection<ShelfItem> ShelfItems => _s.Shelf.Items;

    public bool HasShelf => ShelfItems.Count > 0;

    public string ShelfCountText => ShelfItems.Count == 0 ? "文件架是空的" : $"{ShelfItems.Count} 个文件";

    private void OnShelfChanged()
    {
        OnPropertyChanged(nameof(HasShelf));
        OnPropertyChanged(nameof(ShelfCountText));
    }

    // ── Timers ───────────────────────────────────────────────────────────
    public string CountdownText
    {
        get => _countdownText;
        private set => Set(ref _countdownText, value);
    }

    public double CountdownProgress
    {
        get => _countdownProgress;
        private set => Set(ref _countdownProgress, value);
    }

    public string CountdownLabel
    {
        get => _countdownLabel;
        private set => Set(ref _countdownLabel, value);
    }

    public string CountdownStatusText
    {
        get => _countdownStatusText;
        private set => Set(ref _countdownStatusText, value);
    }

    public string CountdownButtonText
    {
        get => _countdownButtonText;
        private set => Set(ref _countdownButtonText, value);
    }

    public bool CountdownActive
    {
        get => _countdownActive;
        private set => Set(ref _countdownActive, value);
    }

    public bool CountdownPaused
    {
        get => _countdownPaused;
        private set => Set(ref _countdownPaused, value);
    }

    public string StopwatchText
    {
        get => _stopwatchText;
        private set => Set(ref _stopwatchText, value);
    }

    public string StopwatchButtonText
    {
        get => _stopwatchButtonText;
        private set => Set(ref _stopwatchButtonText, value);
    }

    public bool StopwatchRunning
    {
        get => _stopwatchRunning;
        private set => Set(ref _stopwatchRunning, value);
    }

    private void ApplyTimers()
    {
        var timers = _s.Timers;
        CountdownActive = timers.CountdownActive;
        CountdownPaused = timers.CountdownPaused;
        CountdownLabel = timers.CountdownLabel;
        CountdownProgress = timers.CountdownProgress;
        CountdownText = FormatTimer(timers.CountdownRemaining);

        if (timers.CountdownActive)
        {
            CountdownStatusText = timers.CountdownPaused ? "已暂停" : "进行中";
            CountdownButtonText = timers.CountdownPaused ? "继续" : "暂停";
        }
        else
        {
            CountdownStatusText = timers.CountdownTotal > TimeSpan.Zero ? "已结束" : "准备就绪";
            CountdownButtonText = "开始";
        }

        StopwatchRunning = timers.StopwatchRunning;
        StopwatchText = FormatStopwatch(timers.StopwatchElapsed);
        StopwatchButtonText = timers.StopwatchRunning
            ? "暂停"
            : timers.StopwatchElapsed > TimeSpan.Zero ? "继续" : "开始";
    }

    // ── Alerts ───────────────────────────────────────────────────────────
    public ObservableCollection<IslandAlert> Alerts { get; } = new();

    public int UnreadCount
    {
        get => _unreadCount;
        private set
        {
            if (Set(ref _unreadCount, value))
            {
                OnPropertyChanged(nameof(UnreadVisibility));
            }
        }
    }

    public Visibility UnreadVisibility => UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool HasAlerts => Alerts.Count > 0;

    public string AlertCountText => Alerts.Count == 0 ? "暂无通知" : $"{Alerts.Count} 条通知";

    public void AddAlert(IslandAlert alert)
    {
        Alerts.Insert(0, alert);
        while (Alerts.Count > 30)
        {
            Alerts.RemoveAt(Alerts.Count - 1);
        }
    }

    public void RemoveAlert(IslandAlert alert)
    {
        Alerts.Remove(alert);
        UpdateAlertMeta();
    }

    public void ClearAlerts() => Alerts.Clear();

    public void MarkAllRead()
    {
        foreach (var alert in Alerts)
        {
            alert.IsRead = true;
        }

        UpdateAlertMeta();
    }

    private void UpdateAlertMeta()
    {
        UnreadCount = Alerts.Count(a => !a.IsRead);
        OnPropertyChanged(nameof(HasAlerts));
        OnPropertyChanged(nameof(AlertCountText));
    }

    // ── Settings ─────────────────────────────────────────────────────────
    public bool StartWithWindows
    {
        get => _s.Settings.Current.StartWithWindows;
        set
        {
            if (_s.Settings.Current.StartWithWindows == value)
            {
                return;
            }

            _s.Settings.Current.StartWithWindows = value;
            _s.Startup.SetEnabled(value);
            _s.Settings.Save();
            OnPropertyChanged();
        }
    }

    public bool AlwaysOnTop
    {
        get => _s.Settings.Current.AlwaysOnTop;
        set
        {
            if (_s.Settings.Current.AlwaysOnTop == value)
            {
                return;
            }

            _s.Settings.Current.AlwaysOnTop = value;
            _s.Settings.Save();
            App.Island?.ApplyTopmost(value);
            OnPropertyChanged();
        }
    }

    public bool HideInFullscreen
    {
        get => _s.Settings.Current.HideInFullscreen;
        set
        {
            if (_s.Settings.Current.HideInFullscreen == value)
            {
                return;
            }

            _s.Settings.Current.HideInFullscreen = value;
            _s.Settings.Save();
            App.Island?.ApplyFullscreenSetting();
            OnPropertyChanged();
        }
    }

    public bool RecordClipboard
    {
        get => _s.Settings.Current.RecordClipboard;
        set
        {
            if (_s.Settings.Current.RecordClipboard == value)
            {
                return;
            }

            _s.Settings.Current.RecordClipboard = value;
            _s.Clipboard.Enabled = value;
            _s.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ClipboardToggleText));
        }
    }

    public bool PlaySound
    {
        get => _s.Settings.Current.PlaySound;
        set
        {
            if (_s.Settings.Current.PlaySound == value)
            {
                return;
            }

            _s.Settings.Current.PlaySound = value;
            _s.Settings.Save();
            OnPropertyChanged();
        }
    }

    public bool ShowSystemNotifications
    {
        get => _s.Settings.Current.ShowSystemNotifications;
        set
        {
            if (_s.Settings.Current.ShowSystemNotifications == value)
            {
                return;
            }

            _s.Settings.Current.ShowSystemNotifications = value;
            _s.SystemNotifications.Enabled = value;
            _s.Settings.Save();
            OnPropertyChanged();
        }
    }

    public bool EdgeAutoHide
    {
        get => _s.Settings.Current.EdgeAutoHide;
        set
        {
            if (_s.Settings.Current.EdgeAutoHide == value)
            {
                return;
            }

            _s.Settings.Current.EdgeAutoHide = value;
            if (value)
            {
                _s.Settings.Current.SmartAvoidWindows = false;
            }

            _s.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SmartAvoidWindows));
            App.Island?.ApplyVisibilityMode();
        }
    }

    public bool SmartAvoidWindows
    {
        get => _s.Settings.Current.SmartAvoidWindows;
        set
        {
            if (_s.Settings.Current.SmartAvoidWindows == value)
            {
                return;
            }

            _s.Settings.Current.SmartAvoidWindows = value;
            if (value)
            {
                _s.Settings.Current.EdgeAutoHide = false;
            }

            _s.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EdgeAutoHide));
            App.Island?.ApplyVisibilityMode();
        }
    }

    public bool ShowAppSwitches
    {
        get => _s.Settings.Current.ShowAppSwitches;
        set
        {
            if (_s.Settings.Current.ShowAppSwitches == value)
            {
                return;
            }

            _s.Settings.Current.ShowAppSwitches = value;
            _s.Settings.Save();
            OnPropertyChanged();
        }
    }

    public string PipeNameText => NotificationPipeServer.DisplayName;

    public string VersionText => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    // ── Formatting helpers ───────────────────────────────────────────────
    public static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static string FormatTimer(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static string FormatStopwatch(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}"
            : $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}";
    }

    private static string FormatUptime(TimeSpan time)
    {
        return time.TotalDays >= 1
            ? $"{(int)time.TotalDays} 天 {time.Hours} 小时"
            : time.TotalHours >= 1
                ? $"{(int)time.TotalHours} 小时 {time.Minutes} 分"
                : $"{Math.Max(1, time.Minutes)} 分钟";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024d * 1024d)
        {
            return $"{bytesPerSecond / 1024d / 1024d:0.0} MB/s";
        }

        if (bytesPerSecond >= 1024d)
        {
            return $"{bytesPerSecond / 1024d:0} KB/s";
        }

        return $"{bytesPerSecond:0} B/s";
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _positionTimer.Stop();
        _s.Media.Updated -= ApplyMedia;
        _s.Power.Updated -= ApplyPower;
        _s.Monitor.Updated -= ApplyMonitor;
        _s.Sensors.Changed -= ApplySensors;
        _s.Timers.Updated -= ApplyTimers;
    }
}
