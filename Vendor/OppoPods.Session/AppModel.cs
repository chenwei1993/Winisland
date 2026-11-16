using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using OppoPods.Core;
using OppoPods.Session.Bluetooth;

namespace OppoPods.Session;

/// <summary>
/// 应用级协调器(对齐 macOS 版 AppModel):设备仓库 + 会话 + 偏好持久化 + 自动重连调度。
/// 重连延迟序列:常规 [0, 600ms, 1.8s, 4s];系统连接事件后 [300ms, 1.2s, 3s]。
/// </summary>
public sealed class AppModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan[] ReconnectDelays = { TimeSpan.Zero, TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1_800), TimeSpan.FromSeconds(4) };
    private static readonly TimeSpan[] ReconnectDelaysAfterConnect = { TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1_200), TimeSpan.FromSeconds(3) };
    private static readonly TimeSpan DevicePollInterval = TimeSpan.FromSeconds(30);

    private readonly BluetoothDeviceRepository _repository = new();
    private readonly SettingsStore _settings = new();
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private IReadOnlyList<PairedBluetoothDevice> _devices = Array.Empty<PairedBluetoothDevice>();
    private CancellationTokenSource? _reconnectCts;
    private CancellationTokenSource? _pollCts;
    private string _selectedDeviceAddress;
    private bool _autoConnect;
    private string? _connectionPrerequisiteMessage;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>首选耳机连上系统蓝牙(连接边沿,5 秒冷却),供连接弹窗使用。</summary>
    public event Action<ulong>? PreferredDeviceConnected;
    private DateTimeOffset _lastPromptAt = DateTimeOffset.MinValue;

    public BluetoothDeviceRepository Repository => _repository;
    public EarbudsSession Session { get; } = new();

    public AppModel()
    {
        _selectedDeviceAddress = _settings.LoadSelectedDeviceAddress();
        _autoConnect = _settings.LoadAutoConnect();

        _repository.DevicesChanged += () =>
        {
            RefreshDeviceSnapshot();
            SuggestInitialDevice();
            OnPropertyChanged(nameof(Devices));
        };
        _repository.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _repository.Start();
        RefreshDeviceSnapshot();
        SuggestInitialDevice();

        Session.ConnectionStateChanged += _ => OnPropertyChanged(nameof(Session));

        _ = RunDevicePollAsync();
    }

    public string SelectedDeviceAddress
    {
        get => _selectedDeviceAddress;
        set
        {
            if (string.Equals(_selectedDeviceAddress, value, StringComparison.OrdinalIgnoreCase)) return;
            _selectedDeviceAddress = value ?? string.Empty;
            _settings.Save(_selectedDeviceAddress, _autoConnect);
            ConnectionPrerequisiteMessage = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Devices));
            OnPropertyChanged(nameof(SelectedDeviceIsSystemConnected));
        }
    }

    public bool AutoConnect
    {
        get => _autoConnect;
        set
        {
            if (_autoConnect == value) return;
            _autoConnect = value;
            _settings.Save(_selectedDeviceAddress, _autoConnect);
            OnPropertyChanged();
            if (_autoConnect) ScheduleReconnectAttempts(ReconnectDelays);
        }
    }

    public string? ConnectionPrerequisiteMessage
    {
        get => _connectionPrerequisiteMessage;
        private set
        {
            if (string.Equals(_connectionPrerequisiteMessage, value, StringComparison.Ordinal)) return;
            _connectionPrerequisiteMessage = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<PairedBluetoothDevice> Devices => _devices;

    public bool SelectedDeviceIsSystemConnected
    {
        get
        {
            if (string.IsNullOrEmpty(_selectedDeviceAddress)) return false;
            return Devices.Any(d => string.Equals(d.AddressString, _selectedDeviceAddress, StringComparison.OrdinalIgnoreCase) && d.IsConnected);
        }
    }

    /// <summary>首选设备实体(已配对列表中地址匹配者)。</summary>
    public PairedBluetoothDevice? SelectedDevice => _devices.FirstOrDefault(d =>
        string.Equals(d.AddressString, _selectedDeviceAddress, StringComparison.OrdinalIgnoreCase));

    public async Task ConnectSelectedDeviceAsync()
    {
        ConnectionPrerequisiteMessage = null;

        if (string.IsNullOrEmpty(_selectedDeviceAddress))
        {
            ConnectionPrerequisiteMessage = "请先选择耳机。";
            return;
        }
        if (!ulong.TryParse(_selectedDeviceAddress, NumberStyles.HexNumber, null, out var address))
        {
            ConnectionPrerequisiteMessage = "无法读取所选蓝牙设备。";
            return;
        }

        // RFCOMM 只是厂商控制通道,不能替代系统蓝牙连接;每次点击读取实时状态
        if (!await _repository.IsDeviceConnectedAsync(address))
        {
            ConnectionPrerequisiteMessage = "请先在 Windows 系统蓝牙中连接耳机,再连接控制。";
            return;
        }

        var name = SelectedDevice?.DisplayName ?? _selectedDeviceAddress;
        await Session.ConnectAsync(address, name);
    }

    public void Disconnect()
    {
        Session.Disconnect();
    }

    public async void ReconnectIfNeeded()
    {
        if (!AutoConnect) return;
        var selected = Devices.FirstOrDefault(d =>
            string.Equals(d.AddressString, _selectedDeviceAddress, StringComparison.OrdinalIgnoreCase));
        if (selected == null || !selected.IsConnected) return;

        var kind = Session.ConnectionState.Kind;
        if (kind is ConnectionStateKind.Connecting or ConnectionStateKind.Connected) return;
        await ConnectSelectedDeviceAsync();
    }

    public async void RefreshDevices()
    {
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(SelectedDeviceIsSystemConnected));
        if (SelectedDeviceIsSystemConnected) ConnectionPrerequisiteMessage = null;
        await Task.Run(ReconnectIfNeeded);
    }

    private async void OnDeviceConnectionChanged(ulong address)
    {
        RefreshDeviceSnapshot();
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(SelectedDeviceIsSystemConnected));
        if (!string.Equals(address.ToString("X12"), _selectedDeviceAddress, StringComparison.OrdinalIgnoreCase)) return;
        ConnectionPrerequisiteMessage = null;
        // 系统蓝牙链路建立后,厂商 RFCOMM 服务可能稍晚就绪:分阶段重试
        ScheduleReconnectAttempts(ReconnectDelaysAfterConnect);

        // 事件不区分连接/断开,实时查询确认是"连上"边沿后触发弹窗
        try
        {
            if (!await _repository.IsDeviceConnectedAsync(address)) return;
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastPromptAt).TotalSeconds < 5) return;
            _lastPromptAt = now;
            PreferredDeviceConnected?.Invoke(address);
        }
        catch
        {
            // 状态查询失败不弹窗
        }
    }

    private void ScheduleReconnectAttempts(IReadOnlyList<TimeSpan> delays)
    {
        if (!AutoConnect) return;
        CancellationTokenSource newCts;
        CancellationTokenSource? oldCts;
        lock (_reconnectGate)
        {
            oldCts = _reconnectCts;
            _reconnectCts = newCts = new CancellationTokenSource();
        }
        oldCts?.Cancel();
        oldCts?.Dispose();

        foreach (var delay in delays)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, newCts.Token);
                    if (newCts.IsCancellationRequested) return;
                    await RefreshDevicesAsync();
                    ReconnectIfNeeded();
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
    }

    private void RefreshDeviceSnapshot() => _devices = _repository.GetDevices();

    private async Task RefreshDevicesAsync()
    {
        RefreshDeviceSnapshot();
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(SelectedDeviceIsSystemConnected));
        await Task.CompletedTask;
    }

    /// <summary>30 秒安全轮询:兜底感知连接状态变化(对齐 30s 会话刷新周期)。</summary>
    private async Task RunDevicePollAsync()
    {
        _pollCts = new CancellationTokenSource();
        try
        {
            using var timer = new PeriodicTimer(DevicePollInterval);
            while (await timer.WaitForNextTickAsync(_pollCts.Token))
            {
                RefreshDeviceSnapshot();
                OnPropertyChanged(nameof(Devices));
                OnPropertyChanged(nameof(SelectedDeviceIsSystemConnected));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SuggestInitialDevice()
    {
        if (_selectedDeviceAddress.Length > 0) return;
        var suggested = _repository.GetDevices().FirstOrDefault(d =>
            BluetoothDeviceRepository.IsLikelySupported(d.Name));
        if (suggested != null) SelectedDeviceAddress = suggested.AddressString;
    }

    public void Dispose()
    {
        _pollCts?.Cancel();
        _reconnectCts?.Cancel();
        _repository.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _repository.Dispose();
        Session.Dispose();
        _reconnectGate.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
