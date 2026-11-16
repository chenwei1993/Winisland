using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace OppoPods.Session.Bluetooth;

/// <summary>已配对蓝牙设备快照。</summary>
public sealed record PairedBluetoothDevice(ulong Address, string Name, bool IsConnected)
{
    public string AddressString => Address.ToString("X12");

    public string DisplayName => Name.Length > 0 ? Name : AddressString;

    /// <summary>系统蓝牙连接状态文本(下拉列表显示)。</summary>
    public string StateText => IsConnected ? "系统已连接" : "未连接";
}

/// <summary>
/// 已配对设备枚举:DeviceWatcher 维护配对列表与 BluetoothDevice 缓存,
/// ConnectionStatus 从缓存实例实时读取(对齐 macOS 版 pairedDevices() + isConnected())。
/// </summary>
public sealed class BluetoothDeviceRepository : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, BluetoothDevice> _deviceCache = new();
    private readonly Dictionary<ulong, string> _names = new();
    private DeviceWatcher? _watcher;

    public event Action? DevicesChanged;

    /// <summary>任意已配对设备连接/断开状态变化,参数为设备地址。</summary>
    public event Action<ulong>? DeviceConnectionChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_watcher != null) return;
            _watcher = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            _watcher.Added += OnWatcherAdded;
            _watcher.Updated += OnWatcherUpdated;
            _watcher.Removed += OnWatcherRemoved;
        }
        _watcher!.Start();
    }

    public void Stop()
    {
        DeviceWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }
        if (watcher == null) return;
        watcher.Added -= OnWatcherAdded;
        watcher.Updated -= OnWatcherUpdated;
        watcher.Removed -= OnWatcherRemoved;
        try { watcher.Stop(); } catch { /* 已停止 */ }
    }

    public void Dispose() => Stop();

    /// <summary>当前已配对设备快照:名称含 oppo/enco/oneplus/realme 优先,再按显示名排序。</summary>
    public IReadOnlyList<PairedBluetoothDevice> GetDevices()
    {
        List<(PairedBluetoothDevice Device, bool Preferred)> entries;
        lock (_gate)
        {
            entries = _deviceCache.Keys.Select(address =>
            {
                var device = _deviceCache[address];
                _names.TryGetValue(address, out var name);
                if (string.IsNullOrEmpty(name)) name = device.Name;
                return (new PairedBluetoothDevice(address, name, device.ConnectionStatus == BluetoothConnectionStatus.Connected),
                        IsLikelySupported(name));
            }).ToList();
        }
        return entries
            .OrderByDescending(e => e.Preferred)
            .ThenBy(e => e.Device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => e.Device)
            .ToList();
    }

    /// <summary>按地址查询设备实时连接状态(强制 Uncached,避免列表快照过期)。</summary>
    public async Task<bool> IsDeviceConnectedAsync(ulong address)
    {
        BluetoothDevice? device;
        lock (_gate)
        {
            _deviceCache.TryGetValue(address, out device);
        }
        if (device == null)
        {
            device = await BluetoothDevice.FromBluetoothAddressAsync(address);
            if (device == null) return false;
            lock (_gate) _deviceCache[address] = device;
        }
        return device.ConnectionStatus == BluetoothConnectionStatus.Connected;
    }

    private async void OnWatcherAdded(DeviceWatcher sender, DeviceInformation deviceInformation)
    {
        try
        {
            var device = await BluetoothDevice.FromIdAsync(deviceInformation.Id);
            if (device == null) return;
            lock (_gate)
            {
                _deviceCache[device.BluetoothAddress] = device;
                _names[device.BluetoothAddress] = device.Name;
            }
            device.ConnectionStatusChanged += (d, _) => DeviceConnectionChanged?.Invoke(((BluetoothDevice)d).BluetoothAddress);
            DevicesChanged?.Invoke();
        }
        catch
        {
            // 枚举失败的单个设备不影响其余设备
        }
    }

    private void OnWatcherUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        // 属性更新(名称/连接状态)由快照读取缓存实例的实时属性兜底,无需在此处理
    }

    private void OnWatcherRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        // 配对移除的设备无法直接映射地址;保持缓存,由下次快照的实时查询兜底
    }

    public static bool IsLikelySupported(string? name)
    {
        var normalized = (name ?? string.Empty).ToLowerInvariant();
        return normalized.Contains("oppo") || normalized.Contains("enco")
            || normalized.Contains("oneplus") || normalized.Contains("realme");
    }
}
