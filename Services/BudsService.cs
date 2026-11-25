using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
    private readonly SettingsService _settings;
    private readonly DeviceStateStore _store = new();
    private readonly DeviceManager _manager;
    private readonly EventHandler<DeviceStateChangedEventArgs> _stateChangedHandler;
    private readonly EventHandler _knownDevicesChangedHandler;
    private readonly EventHandler<DeviceIdentity> _linkConnectedHandler;
    private readonly CancellationTokenSource _lifetime = new();

    private bool _started;
    private bool _disposed;
    private bool _refreshQueued;
    private bool _leftLowAlerted;
    private bool _rightLowAlerted;
    private bool? _lastLinkUp;
    private bool _connectionBusy;
    private string? _connectionMessage;

    public BudsService(SettingsService settings)
    {
        _settings = settings;
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
            preferredDeviceId: () => _settings.Current.PreferredBudsDeviceId,
            allowExperimental: () => false,
            NullLoggerFactory.Instance,
            disconnected: new PersistedDisconnectedDevices(_settings));

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
        _ = AutoConnectPreferredAsync();
    }

    private DeviceState State => _store.Current;

    private IDeviceProfile? Profile => State.Profile;

    public bool HasDevice => State.Device is not null || _manager.KnownDevices.Count > 0;

    public bool IsConnected => State.Connection == DeviceConnectionState.ControlReady;

    public bool IsLinkUp => State.Connection.IsLinkUp();

    public bool SupportsConnectionActions =>
        State.Device is { Transport: DeviceTransport.Bluetooth };

    public bool CanUseConnectionActions =>
        SupportsConnectionActions &&
        !_connectionBusy &&
        State.Connection is not (
            DeviceConnectionState.AudioConnected or
            DeviceConnectionState.ControlConnecting or
            DeviceConnectionState.Reconnecting);

    public bool IsConnectionBusy => _connectionBusy;

    public string ConnectionActionText
    {
        get
        {
            if (_connectionBusy)
            {
                return "处理中…";
            }

            return State.Connection switch
            {
                DeviceConnectionState.ControlReady => "断开连接",
                DeviceConnectionState.ControlUnavailable => "重新连接",
                DeviceConnectionState.AudioConnected or
                    DeviceConnectionState.ControlConnecting or
                    DeviceConnectionState.Reconnecting => "连接中…",
                DeviceConnectionState.PairedNotConnected or
                    DeviceConnectionState.Disconnected or
                    DeviceConnectionState.DisconnectedByUser => "连接耳机",
                _ => IsLinkUp ? "断开连接" : "连接耳机"
            };
        }
    }

    public bool IsPreferredDevice =>
        State.Device is { Transport: DeviceTransport.Bluetooth } device &&
        string.Equals(_settings.Current.PreferredBudsDeviceId, device.DeviceId, StringComparison.Ordinal);

    public bool CanSetPreferredDevice =>
        State.Device is { Transport: DeviceTransport.Bluetooth } && !IsPreferredDevice;

    public string PreferredButtonText => IsPreferredDevice ? "已设为首选" : "设为首选";

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

            var text = State.Connection switch
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

            return string.IsNullOrWhiteSpace(_connectionMessage)
                ? text
                : $"{text} · {_connectionMessage}";
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

    public ObservableCollection<BudsDeviceOption> Devices { get; } = new();

    public bool HasMultipleDevices => Devices.Count > 1;

    public ObservableCollection<BudsOptionRow> Options { get; } = new();

    public bool HasOptions => IsConnected && Options.Count > 0;

    public bool HasEqualizer => IsConnected && EqualizerOptions.Count > 0;

    public bool CanGameMode => HasCapability(DeviceCapability.GamingMode);
    public bool CanWearDetection => HasCapability(DeviceCapability.InEarDetection);
    public bool CanDualDevice => HasCapability(DeviceCapability.DualDeviceConnection);
    public bool CanFindEarbuds => HasCapability(DeviceCapability.FindMyEarbuds);
    public bool CanFitTest => HasCapability(DeviceCapability.FitTest);

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

    public bool IsFindingEarbuds => State.IsFindingEarbuds;

    public string FindEarbudsText => IsFindingEarbuds ? "停止响铃" : "查找耳机";

    public bool IsFitTestRunning => State.IsFitTestRunning;

    public string FitTestButtonText => IsFitTestRunning ? "检测中…" : "贴合度检测";

    public bool HasFitTestResult => State.FitTest is not null;

    public string FitTestSummary
    {
        get
        {
            var result = State.FitTest;
            if (result is null)
            {
                return string.Empty;
            }

            return $"贴合度：左耳 {FitText(result.Left)} · 右耳 {FitText(result.Right)}";
        }
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

    /// <summary>Writes a brand-specific option the earbuds reported themselves.</summary>
    public void SetOption(string id, int value)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _ = _manager.SetOptionAsync(id, value);
    }

    public void ToggleFindEarbuds()
    {
        if (!IsConnected)
        {
            return;
        }

        _ = _manager.FindEarbudsAsync(!State.IsFindingEarbuds);
    }

    public void RunFitTest()
    {
        if (!IsConnected || State.IsFitTestRunning)
        {
            return;
        }

        _ = _manager.RunFitTestAsync();
    }

    /// <summary>Connects, retries, or disconnects the active Bluetooth earbuds.</summary>
    public async Task ToggleConnectionAsync()
    {
        if (_connectionBusy || State.Device is not { Transport: DeviceTransport.Bluetooth } device)
        {
            return;
        }

        RememberPreferredDevice(device.DeviceId);
        await RunConnectionActionAsync(async () =>
        {
            return State.Connection switch
            {
                DeviceConnectionState.ControlReady =>
                    await _manager.DisconnectDeviceAsync(device.DeviceId),
                DeviceConnectionState.ControlUnavailable =>
                    await RetryOrConnectAsync(device.DeviceId),
                _ => await _manager.ConnectDeviceAsync(device.DeviceId)
            };
        });
    }

    /// <summary>Remembers the active earbuds and optionally connects them if auto-connect is enabled.</summary>
    public void SetPreferredDevice()
    {
        if (State.Device is not { Transport: DeviceTransport.Bluetooth } device)
        {
            return;
        }

        RememberPreferredDevice(device.DeviceId);
        QueueRefresh();
        if (_settings.Current.AutoConnectBuds && !State.Connection.IsLinkUp())
        {
            _ = AutoConnectPreferredAsync(waitForEnumeration: false);
        }
    }

    public async Task SelectDeviceAsync(BudsDeviceOption option)
    {
        RememberPreferredDevice(option.Id);
        await RunConnectionActionAsync(async () =>
        {
            await _manager.SelectDeviceAsync(option.Id);
            return !option.IsConnected
                ? await _manager.ConnectDeviceAsync(option.Id)
                : EncoDeck.Core.Commands.CommandResult.Ok;
        });
    }

    /// <summary>
    /// Connects the remembered earbuds once after startup when auto-connect is enabled.
    /// Devices explicitly disconnected by the user are left alone.
    /// </summary>
    public async Task AutoConnectPreferredAsync(bool waitForEnumeration = true)
    {
        if (!_settings.Current.AutoConnectBuds || _disposed || _connectionBusy)
        {
            return;
        }

        var deviceId = _settings.Current.PreferredBudsDeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        if (waitForEnumeration)
        {
            for (var attempt = 0; attempt < 12 && !_disposed; attempt++)
            {
                if (_manager.KnownDevices.Any(d => string.Equals(
                        d.Identity.DeviceId,
                        deviceId,
                        StringComparison.Ordinal)))
                {
                    break;
                }

                try
                {
                    await Task.Delay(500, _lifetime.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        if (_disposed ||
            !_settings.Current.AutoConnectBuds ||
            !string.Equals(_settings.Current.PreferredBudsDeviceId, deviceId, StringComparison.Ordinal) ||
            (string.Equals(State.Device?.DeviceId, deviceId, StringComparison.Ordinal) &&
             State.Connection.IsLinkUp()))
        {
            return;
        }

        var known = _manager.KnownDevices.FirstOrDefault(d =>
            string.Equals(d.Identity.DeviceId, deviceId, StringComparison.Ordinal));
        if (known is null || known.IsDisconnectedByUser || known.Identity.Transport != DeviceTransport.Bluetooth)
        {
            return;
        }

        await RunConnectionActionAsync(async () =>
        {
            if (known.IsConnected)
            {
                await _manager.SelectDeviceAsync(deviceId);
                return EncoDeck.Core.Commands.CommandResult.Ok;
            }

            return await _manager.ConnectDeviceAsync(deviceId);
        });
    }

    private async Task<EncoDeck.Core.Commands.CommandResult> RetryOrConnectAsync(string deviceId)
    {
        var result = await _manager.ReconnectAsync();
        return result.Status == EncoDeck.Core.Commands.CommandStatus.NotConnected
            ? await _manager.ConnectDeviceAsync(deviceId)
            : result;
    }

    private async Task RunConnectionActionAsync(Func<Task<EncoDeck.Core.Commands.CommandResult>> action)
    {
        if (_connectionBusy)
        {
            return;
        }

        _connectionBusy = true;
        _connectionMessage = null;
        QueueRefresh();
        try
        {
            var result = await action();
            _connectionMessage = result.IsSuccess ? null : DescribeConnectionFailure(result);
        }
        catch
        {
            _connectionMessage = "操作失败，请确认耳机已配对且在附近";
        }
        finally
        {
            _connectionBusy = false;
            QueueRefresh();
        }
    }

    private void RememberPreferredDevice(string deviceId)
    {
        if (string.Equals(_settings.Current.PreferredBudsDeviceId, deviceId, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Current.PreferredBudsDeviceId = deviceId;
        _settings.Save();
    }

    private static string DescribeConnectionFailure(EncoDeck.Core.Commands.CommandResult result) =>
        result.Status switch
        {
            EncoDeck.Core.Commands.CommandStatus.Timeout => "连接超时，请重试",
            EncoDeck.Core.Commands.CommandStatus.TransportError => "蓝牙连接失败，请重试",
            EncoDeck.Core.Commands.CommandStatus.NotConnected => "未找到可连接的耳机",
            EncoDeck.Core.Commands.CommandStatus.Unsupported => "当前耳机不支持此操作",
            _ => "操作失败，请重试"
        };

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

        RefreshDevices();
        RefreshEqualizerOptions();
        RefreshOptions();
        CheckLowBattery();
        UpdateConnectionNotification();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void RefreshOptions()
    {
        var options = IsConnected
            ? State.Options.Values
                .Select(o => o.Value)
                .OrderBy(o => o.Order)
                .ThenBy(o => o.Id, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<DeviceOption>();

        var same = Options.Count == options.Length;
        if (same)
        {
            for (var i = 0; i < options.Length; i++)
            {
                if (!string.Equals(Options[i].Id, options[i].Id, StringComparison.Ordinal) ||
                    Options[i].Choices.Count != options[i].Choices.Count)
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            Options.Clear();
            foreach (var option in options)
            {
                var row = new BudsOptionRow
                {
                    Id = option.Id,
                    Title = LocalizeOptionText(option.LabelKey),
                    Description = option.DescriptionKey is { } descriptionKey
                        ? LocalizeOptionText(descriptionKey)
                        : null,
                    IsRange = option.IsRange,
                    Minimum = option.Minimum,
                    Maximum = option.Maximum,
                    Value = option.Value
                };

                foreach (var choice in option.Choices)
                {
                    row.Choices.Add(new BudsOptionChoice
                    {
                        RowId = option.Id,
                        Value = choice.Value,
                        Name = LocalizeChoice(choice)
                    });
                }

                Options.Add(row);
            }
        }

        foreach (var row in Options)
        {
            var option = options.FirstOrDefault(o => string.Equals(o.Id, row.Id, StringComparison.Ordinal));
            if (option is null)
            {
                continue;
            }

            row.Value = option.Value;
            foreach (var choice in row.Choices)
            {
                choice.IsSelected = choice.Value == option.Value;
            }
        }
    }

    private void RefreshDevices()
    {
        var known = _manager.KnownDevices;
        var same = Devices.Count == known.Count;
        if (same)
        {
            for (var i = 0; i < known.Count; i++)
            {
                if (!string.Equals(Devices[i].Id, known[i].Identity.DeviceId, StringComparison.Ordinal) ||
                    !string.Equals(Devices[i].Name, known[i].Identity.Name, StringComparison.Ordinal) ||
                    Devices[i].IsConnected != known[i].IsConnected ||
                    Devices[i].IsActive != known[i].IsActive ||
                    Devices[i].IsPreferred != string.Equals(
                        _settings.Current.PreferredBudsDeviceId,
                        known[i].Identity.DeviceId,
                        StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            Devices.Clear();
            foreach (var device in known)
            {
                Devices.Add(new BudsDeviceOption
                {
                    Id = device.Identity.DeviceId,
                    Name = device.Identity.Name,
                    IsConnected = device.IsConnected,
                    IsActive = device.IsActive,
                    IsPreferred = string.Equals(
                        _settings.Current.PreferredBudsDeviceId,
                        device.Identity.DeviceId,
                        StringComparison.Ordinal)
                });
            }
        }
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

    private static readonly Dictionary<string, string> OptionText = new(StringComparer.Ordinal)
    {
        ["AutoOff_Title"] = "自动关机",
        ["AutoOff_Off"] = "关闭",
        ["AutoOff_WhenRemoved"] = "摘下时关闭",
        ["AutoOff_Minutes"] = "{0} 分钟后",
        ["AutoOff_IdleDescription"] = "耳机闲置达到所选时长后自动关机",
        ["PowerOff_Timer"] = "定时关机",
        ["PowerOff_Timer_Description"] = "到达所选时间后自动关闭耳机",
        ["SonyOption_AmbientLevel"] = "环境声级别",
        ["SonyOption_Unknown"] = "未知（0x{0}）",
        ["SonyKey_Left"] = "左耳触控",
        ["SonyKey_Right"] = "右耳触控",
        ["SonyKey_Custom"] = "自定义按键",
        ["SonyKey_NcAmbient"] = "降噪 / 环境声按键",
        ["SonyKey_Other"] = "可分配按键",
        ["SonyPreset_00"] = "环境声控制",
        ["SonyPreset_10"] = "音量控制",
        ["SonyPreset_20"] = "播放控制",
        ["SonyPreset_21"] = "曲目控制",
        ["SonyPreset_30"] = "语音助手",
        ["SonyPreset_31"] = "Google 助理",
        ["SonyPreset_32"] = "Amazon Alexa",
        ["SonyPreset_35"] = "环境声控制 / 快速访问",
        ["SonyPreset_36"] = "快速访问",
        ["SonyPreset_45"] = "环境声控制（麦克风）",
        ["SonyPreset_46"] = "聆听模式 / 快速访问",
        ["SonyPreset_47"] = "环境声控制 / 聆听模式",
        ["SonyPreset_FF"] = "无功能"
    };

    private static string LocalizeOptionText(string key) =>
        OptionText.TryGetValue(key, out var text) ? text : key;

    private static string FitText(FitQuality quality) => quality switch
    {
        FitQuality.Good => "良好",
        FitQuality.Average => "一般",
        FitQuality.Poor => "较差",
        FitQuality.Failed => "失败",
        _ => "未知"
    };

    private static string LocalizeChoice(DeviceOptionChoice choice)
    {
        var text = LocalizeOptionText(choice.LabelKey);
        return string.IsNullOrEmpty(choice.Argument)
            ? text
            : string.Format(CultureInfo.CurrentCulture, text, choice.Argument);
    }

    private static string FormatBattery(int? level) => level is null ? "--" : $"{level}%";

    private sealed class PersistedDisconnectedDevices : IUserDisconnectedDevices
    {
        private readonly SettingsService _settings;
        private readonly object _gate = new();

        public PersistedDisconnectedDevices(SettingsService settings)
        {
            _settings = settings;
        }

        public bool Contains(string deviceId)
        {
            lock (_gate)
            {
                return _settings.Current.BudsDisconnectedDeviceIds?.Any(id =>
                    string.Equals(id, deviceId, StringComparison.Ordinal)) == true;
            }
        }

        public void Set(string deviceId, bool disconnected)
        {
            lock (_gate)
            {
                var ids = _settings.Current.BudsDisconnectedDeviceIds ??= new();
                var index = ids.FindIndex(id => string.Equals(id, deviceId, StringComparison.Ordinal));
                if (disconnected)
                {
                    if (index < 0)
                    {
                        ids.Add(deviceId);
                        _settings.Save();
                    }
                }
                else if (index >= 0)
                {
                    ids.RemoveAt(index);
                    _settings.Save();
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started = false;
        _lifetime.Cancel();
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

        _lifetime.Dispose();
    }
}
