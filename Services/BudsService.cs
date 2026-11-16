using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using EncoDeck.Bluetooth;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using Microsoft.Extensions.Logging.Abstractions;
using WinIsland.Models;

namespace WinIsland.Services;

/// <summary>
/// Multi-brand earbuds control built on EncoDeck's device engine. It discovers
/// paired devices, opens the vendor control channel, and projects the shared
/// device state into the island's small control card.
/// </summary>
public sealed class BudsService : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DeviceStateStore _store = new();
    private readonly DeviceManager _manager;
    private readonly EventHandler<DeviceStateChangedEventArgs> _stateChangedHandler;
    private readonly EventHandler _knownDevicesChangedHandler;
    private readonly EventHandler<DeviceIdentity> _linkConnectedHandler;

    private bool _started;
    private bool _disposed;
    private bool _refreshQueued;
    private bool _leftLowAlerted;
    private bool _rightLowAlerted;
    private bool? _lastLinkUp;

    public BudsService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        LeAudioOnlyDevices.Configure(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinIsland",
            "le-audio-devices.json"));

        var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());
        _manager = new DeviceManager(
            registry,
            _store,
            new RfcommChannelConnector(registry),
            preferredDeviceId: () => null,
            allowExperimental: () => false,
            NullLoggerFactory.Instance);

        _stateChangedHandler = (_, _) => QueueRefresh();
        _knownDevicesChangedHandler = (_, _) => QueueRefresh();
        _linkConnectedHandler = (_, _) => QueueRefresh();

        _store.StateChanged += _stateChangedHandler;
        _manager.KnownDevicesChanged += _knownDevicesChangedHandler;
        _manager.ActiveDeviceLinkConnected += _linkConnectedHandler;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the active earbuds connect or disconnect.</summary>
    public event Action<bool, string>? ConnectionChanged;

    /// <summary>Raised once when an earbud crosses below 20% while discharging.</summary>
    public event Action<string, int>? LowBattery;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _manager.Start();
    }

    private DeviceState State => _store.Current;

    private IDeviceProfile? Profile => State.Profile;

    public bool HasDevice => State.Device is not null || _manager.KnownDevices.Count > 0;

    public bool IsConnected => State.Connection == DeviceConnectionState.ControlReady;

    public string DeviceName
    {
        get
        {
            var name = State.Device?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Profile?.DisplayName;
            }

            return string.IsNullOrWhiteSpace(name) ? "耳机" : name;
        }
    }

    public string ProductIdText =>
        State.ProductId is { } productId
            ? $"产品 ID {productId:X6}"
            : Profile?.DisplayName ?? "未识别型号";

    public string StatusText
    {
        get
        {
            if (!HasDevice)
            {
                return "未找到已配对的耳机";
            }

            return State.Connection switch
            {
                DeviceConnectionState.PairedNotConnected => "已配对，等待连接",
                DeviceConnectionState.AudioConnected => "已连接，正在打开控制通道…",
                DeviceConnectionState.ControlConnecting => "正在连接控制通道…",
                DeviceConnectionState.ControlReady => $"已连接 · {DetailText}",
                DeviceConnectionState.ControlUnavailable => "控制通道不可用",
                DeviceConnectionState.Reconnecting => "连接丢失，正在重连…",
                DeviceConnectionState.Disconnected => "已断开",
                DeviceConnectionState.DisconnectedByUser => "已在应用中断开",
                DeviceConnectionState.UsbReceiverConnected => "USB 接收器已连接",
                DeviceConnectionState.UsbHeadsetConnected => $"USB 耳机已连接 · {DetailText}",
                _ => "等待系统蓝牙连接"
            };
        }
    }

    public bool HasBattery =>
        IsConnected && (LeftBattery is not null || RightBattery is not null);

    public int? LeftBattery => State.Battery.Left?.Value.Level;
    public int? RightBattery => State.Battery.Right?.Value.Level;
    public int? CaseBattery => State.Battery.Case?.Value.Level;
    public bool LeftCharging => State.Battery.Left?.Value.IsCharging == true;
    public bool RightCharging => State.Battery.Right?.Value.IsCharging == true;
    public bool CaseCharging => State.Battery.Case?.Value.IsCharging == true;

    public string LeftBatteryText => FormatBattery(LeftBattery);
    public string RightBatteryText => FormatBattery(RightBattery);
    public string CaseBatteryText => FormatBattery(CaseBattery);

    public string CompactText
    {
        get
        {
            var levels = new[] { LeftBattery, RightBattery }.Where(v => v is not null).ToArray();
            return levels.Length == 0 ? "--" : $"{levels.Min()!.Value}%";
        }
    }

    public string DetailText
    {
        get
        {
            if (!IsConnected)
            {
                return "未连接";
            }

            return $"左 {LeftBatteryText} · 右 {RightBatteryText} · 盒 {CaseBatteryText}";
        }
    }

    public NoiseControlMode NoiseMode => State.NoiseControl?.Value.Mode ?? NoiseControlMode.Off;

    public bool IsNoiseOff => NoiseMode == NoiseControlMode.Off;
    public bool IsNoiseCancellation => NoiseMode == NoiseControlMode.NoiseCancellation;
    public bool IsNoiseTransparency => NoiseMode == NoiseControlMode.Transparency;
    public bool IsNoiseAdaptive => NoiseMode == NoiseControlMode.Adaptive;

    public bool CanNoiseControl => HasCapability(DeviceCapability.NoiseCancellation);

    public bool CanNoiseCancellation => Profile?.NoiseControlOptions.Any(o =>
        o.Mode == NoiseControlMode.NoiseCancellation && o.Support != CapabilitySupport.Unsupported) == true;

    public bool CanNoiseTransparency => Profile?.NoiseControlOptions.Any(o =>
        o.Mode == NoiseControlMode.Transparency && o.Support != CapabilitySupport.Unsupported) == true;

    public bool CanNoiseAdaptive => Profile?.NoiseControlOptions.Any(o =>
        o.Mode == NoiseControlMode.Adaptive && o.Support != CapabilitySupport.Unsupported) == true;

    public ObservableCollection<BudsEqualizerOption> EqualizerOptions { get; } = new();

    public bool HasEqualizer => IsConnected && EqualizerOptions.Count > 0;

    public bool CanGameMode => HasCapability(DeviceCapability.GamingMode);
    public bool CanWearDetection => HasCapability(DeviceCapability.InEarDetection);
    public bool CanDualDevice => HasCapability(DeviceCapability.DualDeviceConnection);

    public bool CanSpatialAudioModes => HasCapability(DeviceCapability.HeadTracking);

    public bool CanSpatialSound =>
        HasCapability(DeviceCapability.SpatialAudio) && !CanSpatialAudioModes;

    public bool SpatialSound
    {
        get => Toggle(DeviceToggle.SpatialAudio) == true;
        set => SetToggle(DeviceToggle.SpatialAudio, value);
    }

    public SpatialAudioMode SpatialAudioMode => State.Spatial?.Value ?? SpatialAudioMode.Off;
    public bool IsSpatialAudioOff => SpatialAudioMode == SpatialAudioMode.Off;
    public bool IsSpatialAudioFixed => SpatialAudioMode == SpatialAudioMode.Fixed;
    public bool IsSpatialAudioHeadTracking => SpatialAudioMode == SpatialAudioMode.HeadTracking;

    public bool GameMode
    {
        get => Toggle(DeviceToggle.GamingMode) == true;
        set => SetToggle(DeviceToggle.GamingMode, value);
    }

    public bool WearDetection
    {
        get => Toggle(DeviceToggle.InEarDetection) == true;
        set => SetToggle(DeviceToggle.InEarDetection, value);
    }

    public bool DualDevice
    {
        get => Toggle(DeviceToggle.DualDeviceConnection) == true;
        set => SetToggle(DeviceToggle.DualDeviceConnection, value);
    }

    public void SetNoiseControl(NoiseControlMode mode)
    {
        if (!IsConnected)
        {
            return;
        }

        _ = _manager.SetNoiseControlAsync(new NoiseControlState(mode));
        QueueRefresh();
    }

    public void SetEqualizer(string presetId)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(presetId))
        {
            return;
        }

        _ = _manager.SetEqualizerPresetAsync(presetId);
        QueueRefresh();
    }

    public void SetSpatialAudio(SpatialAudioMode mode)
    {
        if (!IsConnected)
        {
            return;
        }

        _ = _manager.SetSpatialAudioAsync(mode);
        QueueRefresh();
    }

    public async Task ReconnectAsync()
    {
        try
        {
            if (State.Device is { } device)
            {
                await _manager.ConnectDeviceAsync(device.DeviceId);
            }
            else
            {
                await _manager.ReconnectAsync();
            }
        }
        catch
        {
            // The state store exposes the failure.
        }

        QueueRefresh();
    }

    public void Refresh()
    {
        if (IsConnected)
        {
            _ = _manager.RefreshAsync();
        }
    }

    private bool HasCapability(DeviceCapability capability) =>
        IsConnected && Profile?.Capabilities.IsAvailable(capability, allowExperimental: false) == true;

    private bool? Toggle(DeviceToggle toggle) =>
        State.Toggles.TryGetValue(toggle, out var value) ? value.Value : null;

    private void SetToggle(DeviceToggle toggle, bool value)
    {
        if (!IsConnected || Toggle(toggle) == value)
        {
            return;
        }

        _ = _manager.SetToggleAsync(toggle, value);
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            RefreshCore();
            return;
        }

        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            RefreshCore();
        }));
    }

    private void RefreshCore()
    {
        if (_disposed)
        {
            return;
        }

        RefreshEqualizerOptions();
        CheckLowBattery();
        UpdateConnectionNotification();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void RefreshEqualizerOptions()
    {
        var presets = IsConnected && Profile is { } profile
            ? profile.EqualizerPresets
                .Where(p => p.Support != CapabilitySupport.Unsupported)
                .ToArray()
            : Array.Empty<EqualizerPreset>();

        var same = EqualizerOptions.Count == presets.Length;
        if (same)
        {
            for (var i = 0; i < presets.Length; i++)
            {
                if (!string.Equals(EqualizerOptions[i].Id, presets[i].Id, StringComparison.Ordinal) ||
                    !string.Equals(EqualizerOptions[i].Name, presets[i].DisplayName, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            EqualizerOptions.Clear();
            foreach (var preset in presets)
            {
                EqualizerOptions.Add(new BudsEqualizerOption
                {
                    Id = preset.Id,
                    Name = LocalizeEqualizerName(preset.Id, preset.DisplayName)
                });
            }
        }

        var selected = State.Equalizer?.Value.ActivePresetId;
        foreach (var option in EqualizerOptions)
        {
            option.IsSelected = string.Equals(option.Id, selected, StringComparison.Ordinal);
        }
    }

    private void UpdateConnectionNotification()
    {
        var up = State.Connection.IsLinkUp();
        if (_lastLinkUp is null)
        {
            _lastLinkUp = up;
            return;
        }

        if (_lastLinkUp == up)
        {
            return;
        }

        _lastLinkUp = up;
        var name = DeviceName;
        PostToUi(() => ConnectionChanged?.Invoke(up, name));
    }

    private void CheckLowBattery()
    {
        if (!IsConnected)
        {
            _leftLowAlerted = false;
            _rightLowAlerted = false;
            return;
        }

        CheckSide(State.Battery.Left?.Value, "左耳", ref _leftLowAlerted);
        CheckSide(State.Battery.Right?.Value, "右耳", ref _rightLowAlerted);
    }

    private void CheckSide(BatteryReading? battery, string side, ref bool alerted)
    {
        if (battery?.Level is not { } level)
        {
            return;
        }

        if (battery.IsCharging == true || level > 25)
        {
            alerted = false;
            return;
        }

        if (level <= 20 && !alerted)
        {
            alerted = true;
            LowBattery?.Invoke(side, level);
        }
    }

    private void PostToUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }

    private static string LocalizeEqualizerName(string id, string fallback)
    {
        return id switch
        {
            "eq-22" => "纯原音",
            "eq-31" => "脉冲低音",
            "eq-32" => "悠扬人声",
            _ => fallback
        };
    }

    private static string FormatBattery(int? level) => level is null ? "--" : $"{level}%";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started = false;
        _store.StateChanged -= _stateChangedHandler;
        _manager.KnownDevicesChanged -= _knownDevicesChangedHandler;
        _manager.ActiveDeviceLinkConnected -= _linkConnectedHandler;

        try
        {
            _manager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best effort shutdown.
        }
    }
}
