using System.ComponentModel;
using System.Windows.Threading;
using OppoPods.Core;
using OppoPods.Session;

namespace WinIsland.Services;

/// <summary>
/// Bridges the vendored OPPO/OnePlus/realme RFCOMM control session into the
/// island. The upstream session raises updates on WinRT threads, so every
/// notification is marshalled back to the WPF dispatcher here.
/// </summary>
public sealed class BudsService : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly AppModel _model = new();

    private bool _started;
    private bool _disposed;
    private bool _refreshQueued;
    private bool _leftLowAlerted;
    private bool _rightLowAlerted;

    public BudsService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _model.PropertyChanged += OnModelPropertyChanged;
        _model.Session.PropertyChanged += OnSessionPropertyChanged;
        _model.Session.ConnectionStateChanged += _ => QueueRefresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised once when an earbud crosses below 20% while discharging.</summary>
    public event Action<string, int>? LowBattery;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _model.AutoConnect = true;
        _ = ConnectInitialAsync();
    }

    public bool HasDevice =>
        _model.SelectedDevice is not null ||
        !string.IsNullOrEmpty(_model.SelectedDeviceAddress);

    public bool IsConnected =>
        _model.Session.ConnectionState.Kind == ConnectionStateKind.Connected;

    public string DeviceName
    {
        get
        {
            var name = _model.Session.DeviceName;
            if (string.IsNullOrWhiteSpace(name) || name == "未知设备")
            {
                name = _model.SelectedDevice?.DisplayName ?? "OPPO 耳机";
            }

            return name;
        }
    }

    public string ProductIdText =>
        string.IsNullOrWhiteSpace(_model.Session.ProductID)
            ? "未识别型号"
            : $"产品 ID {_model.Session.ProductID}";

    public string StatusText
    {
        get
        {
            if (!HasDevice)
            {
                return "未找到已配对的 OPPO / OnePlus / realme 耳机";
            }

            if (IsConnected)
            {
                return $"已连接 · {DetailText}";
            }

            if (!string.IsNullOrWhiteSpace(_model.ConnectionPrerequisiteMessage))
            {
                return _model.ConnectionPrerequisiteMessage;
            }

            return _model.Session.ConnectionState.Kind switch
            {
                ConnectionStateKind.Connecting => "正在连接控制通道…",
                ConnectionStateKind.Failed => "连接失败：" +
                    (_model.Session.ConnectionState.Error ?? "请确认耳机已连接系统蓝牙"),
                _ => "等待系统蓝牙连接"
            };
        }
    }

    public bool HasBattery =>
        IsConnected && (LeftBattery is not null || RightBattery is not null);

    public int? LeftBattery => _model.Session.Battery.Left?.Level;
    public int? RightBattery => _model.Session.Battery.Right?.Level;
    public int? CaseBattery => _model.Session.Battery.ChargingCase?.Level;
    public bool LeftCharging => _model.Session.Battery.Left?.IsCharging == true;
    public bool RightCharging => _model.Session.Battery.Right?.IsCharging == true;
    public bool CaseCharging => _model.Session.Battery.ChargingCase?.IsCharging == true;

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

    public NoiseControlMode NoiseMode => _model.Session.NoiseControl;

    public bool IsNoiseOff => NoiseMode == NoiseControlMode.Off;
    public bool IsNoiseCancellation => NoiseMode == NoiseControlMode.NoiseCancellation;
    public bool IsNoiseTransparency => NoiseMode == NoiseControlMode.Transparency;
    public bool IsNoiseAdaptive => NoiseMode == NoiseControlMode.Adaptive;

    public bool CanNoiseControl => IsConnected &&
        _model.Session.Capabilities.IndexByNoiseMode.Count > 0;

    public bool CanNoiseCancellation => CanNoiseControl &&
        _model.Session.Capabilities.IndexByNoiseMode.ContainsKey(NoiseControlMode.NoiseCancellation);

    public bool CanNoiseTransparency => CanNoiseControl &&
        _model.Session.Capabilities.IndexByNoiseMode.ContainsKey(NoiseControlMode.Transparency);

    public bool CanNoiseAdaptive => CanNoiseControl &&
        _model.Session.Capabilities.IndexByNoiseMode.ContainsKey(NoiseControlMode.Adaptive);

    public bool CanGameMode => IsConnected && _model.Session.Capabilities.HasGameMode;
    public bool CanWearDetection => IsConnected && _model.Session.Capabilities.HasWearDetection;
    public bool CanDualDevice => IsConnected && _model.Session.Capabilities.HasDualDevice;
    public bool CanSpatialSound => IsConnected && _model.Session.Capabilities.HasSpatialSound;

    public bool GameMode
    {
        get => _model.Session.GameMode;
        set
        {
            if (IsConnected && _model.Session.GameMode != value)
            {
                _model.Session.SetGameMode(value);
                QueueRefresh();
            }
        }
    }

    public bool WearDetection
    {
        get => _model.Session.WearDetection;
        set
        {
            if (IsConnected && _model.Session.WearDetection != value)
            {
                _model.Session.SetWearDetection(value);
                QueueRefresh();
            }
        }
    }

    public bool DualDevice
    {
        get => _model.Session.DualDevice;
        set
        {
            if (IsConnected && _model.Session.DualDevice != value)
            {
                _model.Session.SetDualDevice(value);
                QueueRefresh();
            }
        }
    }

    public bool SpatialSound
    {
        get => _model.Session.SpatialSound;
        set
        {
            if (IsConnected && _model.Session.SpatialSound != value)
            {
                _model.Session.SetSpatialSound(value);
                QueueRefresh();
            }
        }
    }

    public void SetNoiseControl(NoiseControlMode mode)
    {
        if (!IsConnected)
        {
            return;
        }

        _model.Session.SetNoiseControl(mode);
        QueueRefresh();
    }

    public async Task ReconnectAsync()
    {
        try
        {
            _model.RefreshDevices();
            await _model.ConnectSelectedDeviceAsync();
        }
        catch
        {
            // The session exposes the failure through its state.
        }

        QueueRefresh();
    }

    public void Refresh()
    {
        if (IsConnected)
        {
            _model.Session.Refresh();
        }
    }

    private async Task ConnectInitialAsync()
    {
        try
        {
            await Task.Delay(2_000);
            if (!_started || _disposed)
            {
                return;
            }

            _model.RefreshDevices();
            if (_model.SelectedDeviceIsSystemConnected)
            {
                await _model.ConnectSelectedDeviceAsync();
            }
        }
        catch
        {
            // The card will show the session state instead.
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => QueueRefresh();

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e) => QueueRefresh();

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

        CheckLowBattery();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void CheckLowBattery()
    {
        if (!IsConnected)
        {
            _leftLowAlerted = false;
            _rightLowAlerted = false;
            return;
        }

        CheckSide(_model.Session.Battery.Left, "左耳", ref _leftLowAlerted);
        CheckSide(_model.Session.Battery.Right, "右耳", ref _rightLowAlerted);
    }

    private void CheckSide(BatteryLevel? battery, string side, ref bool alerted)
    {
        if (battery is null)
        {
            return;
        }

        if (battery.IsCharging || battery.Level > 25)
        {
            alerted = false;
            return;
        }

        if (battery.Level <= 20 && !alerted)
        {
            alerted = true;
            LowBattery?.Invoke(side, battery.Level);
        }
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
        _model.PropertyChanged -= OnModelPropertyChanged;
        _model.Session.PropertyChanged -= OnSessionPropertyChanged;
        _model.Dispose();
    }
}
