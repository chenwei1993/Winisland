using System.ComponentModel;
using System.Runtime.CompilerServices;
using OppoPods.Core;
using OppoPods.Session.Bluetooth;

namespace OppoPods.Session;

/// <summary>
/// 耳机会话状态机(对齐 macOS 版 EarbudsSession):
/// 连接 10 秒超时;初始化握手 → 查询,250ms/1s/2.5s 三轮重发;30 秒周期刷新;
/// 0x8200 → 立即 0x0205 订阅;EQ 保存/删除后 450ms 防抖刷新;
/// 游戏模式 featureID 动态:0x28 优先,回退 0x06。
/// </summary>
public sealed class EarbudsSession : INotifyPropertyChanged, IDisposable
{
    private const int GameModeFeatureNew = 0x28;
    private const int GameModeFeatureLegacy = 0x06;
    private static readonly int[] InitializationResendDelays = { 250, 1_000, 2_500 };
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EqualizerRefreshDebounce = TimeSpan.FromMilliseconds(450);

    private readonly object _gate = new();
    private readonly IRfcommTransport _transport;
    private readonly OppoCommands _commands = new();
    private readonly OppoPacketFramer _framer = new();
    private readonly DeviceModelRegistry _modelRegistry = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _timeoutCts;
    private CancellationTokenSource? _equalizerRefreshCts;

    private ConnectionState _connectionState = ConnectionState.Disconnected;
    private string _deviceName = "未连接";
    private string? _productID;
    private BatteryStatus _battery = new();
    private NoiseControlMode _noiseControl = NoiseControlMode.Off;
    private SpatialAudioMode _spatialAudio = SpatialAudioMode.Off;
    private bool _gameMode;
    private bool _wearDetection;
    private bool _dualDevice;
    private bool _spatialSound;
    private int _selectedEQ;
    private List<DeviceEqualizerPreset> _deviceEQPresets = new();
    private DeviceCapabilities _capabilities = DeviceCapabilities.Generic;
    private int _gameModeFeatureID = GameModeFeatureLegacy;
    private int _generation;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ConnectionState>? ConnectionStateChanged;

    public EarbudsSession(IRfcommTransport? transport = null)
    {
        _transport = transport ?? new WinRtRfcommTransport();
        _transport.OnData += OnTransportData;
        _transport.OnClosed += OnTransportClosed;
    }

    public ConnectionState ConnectionState { get => _connectionState; private set => Set(ref _connectionState, value); }
    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }
    public string? ProductID { get => _productID; private set => Set(ref _productID, value); }
    public BatteryStatus Battery { get => _battery; private set => Set(ref _battery, value); }
    public NoiseControlMode NoiseControl { get => _noiseControl; private set => Set(ref _noiseControl, value); }
    public SpatialAudioMode SpatialAudio { get => _spatialAudio; private set => Set(ref _spatialAudio, value); }
    public bool GameMode { get => _gameMode; private set => Set(ref _gameMode, value); }
    public bool WearDetection { get => _wearDetection; private set => Set(ref _wearDetection, value); }
    public bool DualDevice { get => _dualDevice; private set => Set(ref _dualDevice, value); }
    public bool SpatialSound { get => _spatialSound; private set => Set(ref _spatialSound, value); }
    public int SelectedEQ { get => _selectedEQ; private set => Set(ref _selectedEQ, value); }
    public DeviceCapabilities Capabilities { get => _capabilities; private set => Set(ref _capabilities, value); }

    public IReadOnlyList<DeviceEqualizerPreset> DeviceEQPresets
    {
        get { lock (_gate) return _deviceEQPresets; }
        private set { lock (_gate) _deviceEQPresets = value.ToList(); }
    }

    /// <summary>内置 + 设备端自定义 EQ 预设(对齐 macOS 版 equalizerPresets)。</summary>
    public IReadOnlyList<EqualizerPreset> EqualizerPresets
    {
        get
        {
            lock (_gate)
            {
                var builtInIDs = _capabilities.EqualizerPresets.Select(p => p.Id).ToHashSet();
                var custom = _deviceEQPresets
                    .Where(p => !builtInIDs.Contains(p.Id) && p.Name.Length > 0)
                    .Select(p => new EqualizerPreset(p.Id, p.Name));
                return _capabilities.EqualizerPresets.Concat(custom).ToList();
            }
        }
    }

    public int[] CustomEQFrequencies
    {
        get
        {
            lock (_gate)
            {
                var builtInIDs = _capabilities.EqualizerPresets.Select(p => p.Id).ToHashSet();
                var actual = _deviceEQPresets
                    .FirstOrDefault(p => !builtInIDs.Contains(p.Id) && p.Frequencies.Length > 0)?.Frequencies;
                if (actual is { Length: > 0 }) return actual;
                return _capabilities.CustomEQFrequencies.Count == 0
                    ? new[] { 62, 250, 1_000, 4_000, 8_000, 16_000 }
                    : _capabilities.CustomEQFrequencies.ToArray();
            }
        }
    }

    public async Task ConnectAsync(ulong address, string deviceName)
    {
        CancellationTokenSource? timeoutCts;
        lock (_gate)
        {
            if (_connectionState.Kind == ConnectionStateKind.Connecting) return;
            CancelInitializationTasksLocked();
            _generation++;
            _connectionState = ConnectionState.Connecting;
            _deviceName = deviceName;

            // Some firmware does not return a product id; the advertised model
            // name is still enough to resolve EQ / spatial capabilities.
            if (_modelRegistry.CapabilitiesForName(deviceName) is { } byName)
            {
                Capabilities = byName;
            }

            _sessionCts = new CancellationTokenSource();
            timeoutCts = _timeoutCts = new CancellationTokenSource();
        }
        OnPropertyChanged(string.Empty);

        // 10 秒超时:到期仍连接中则关闭并按错误提示结束
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SessionTimeout, timeoutCts.Token);
                CancellationTokenSource? sessionCts;
                lock (_gate)
                {
                    if (_connectionState.Kind != ConnectionStateKind.Connecting)
                    {
                        _timeoutCts = null;
                        return;
                    }
                    sessionCts = _sessionCts;
                }
                _transport.Close();
                sessionCts?.Cancel();
                ConnectionState = ConnectionState.FailedState("控制通道连接超时,请稍后重试。");
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            await _transport.ConnectAsync(address, _sessionCts!.Token);
            lock (_gate)
            {
                if (_connectionState.Kind != ConnectionStateKind.Connecting) return;
                _connectionState = ConnectionState.Connected;
                _timeoutCts?.Cancel();
                _timeoutCts = null;
            }
            OnPropertyChanged(nameof(ConnectionState));
            BeginSession();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (_connectionState.Kind != ConnectionStateKind.Connecting) return;
            }
            ConnectionState = ConnectionState.FailedState(error.Message);
        }
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            CancelInitializationTasksLocked();
            _sessionCts?.Cancel();
            _sessionCts = null;
        }
        _transport.Close();
        ResetState();
    }

    public void Refresh()
    {
        if (ConnectionState.Kind != ConnectionStateKind.Connected) return;
        Send(_commands.QueryStatus());
        Send(_commands.QueryBattery());
        Send(_commands.QueryANC());
        Send(_commands.QueryEQ());
        if (Capabilities.HasCustomEQ) Send(_commands.QueryAllEQ());
    }

    // ---- 写操作:乐观更新后发包(对齐 macOS 版) ----

    public void SetNoiseControl(NoiseControlMode mode)
    {
        NoiseControl = mode;
        var index = Capabilities.IndexByNoiseMode.GetValueOrDefault(mode, -1);
        Send(index >= 0 ? _commands.SetNoiseControl(index) : _commands.SetNoiseControl(mode));
    }

    public void SetSpatialAudio(SpatialAudioMode mode)
    {
        SpatialAudio = mode;
        Send(_commands.SetSpatialAudio(mode));
    }

    public void SetGameMode(bool enabled)
    {
        GameMode = enabled;
        Send(_commands.SetFeature((byte)_gameModeFeatureID, enabled));
    }

    public void SetWearDetection(bool enabled)
    {
        WearDetection = enabled;
        Send(_commands.SetFeature(0x04, enabled));
    }

    public void SetDualDevice(bool enabled)
    {
        DualDevice = enabled;
        Send(_commands.SetFeature(0x11, enabled));
    }

    public void SetSpatialSound(bool enabled)
    {
        SpatialSound = enabled;
        Send(_commands.SetFeature(0x1B, enabled));
    }

    public void SetEQ(int presetId)
    {
        SelectedEQ = presetId;
        Send(_commands.SetEQ(presetId));
    }

    public void RefreshCustomEQ()
    {
        if (!Capabilities.HasCustomEQ) return;
        Send(_commands.QueryEQ());
        Send(_commands.QueryAllEQ());
    }

    public void SaveCustomEQ(int id, string name, IReadOnlyList<int> frequencies, IReadOnlyList<int> gains,
        int minimumGain, int maximumGain)
    {
        if (!Capabilities.HasCustomEQ || name.Trim().Length == 0) return;
        Send(_commands.SaveCustomEQ(id, name, frequencies, gains, minimumGain, maximumGain));
        ScheduleEqualizerRefresh();
    }

    public void DeleteCustomEQ(DeviceEqualizerPreset preset)
    {
        if (!Capabilities.HasCustomEQ || preset.Id <= 0) return;
        Send(_commands.DeleteCustomEQ(preset));
        lock (_gate) _deviceEQPresets.RemoveAll(p => p.Id == preset.Id);
        OnPropertyChanged(nameof(DeviceEQPresets));
        ScheduleEqualizerRefresh();
    }

    // ---- 会话初始化与刷新 ----

    private void BeginSession()
    {
        Send(_commands.Handshake());
        Send(_commands.QueryProductID());
        Send(_commands.QueryBroadcastCodes());
        Refresh();

        // 控制通道可能延迟就绪:分阶段重发初始化查询,不要求用户先断开再连接耳机
        var token = _sessionCts?.Token ?? CancellationToken.None;
        foreach (var delay in InitializationResendDelays)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token);
                    if (ConnectionState.Kind != ConnectionStateKind.Connected) return;
                    Send(_commands.QueryProductID());
                    Send(_commands.QueryBroadcastCodes());
                    Refresh();
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(RefreshInterval);
                while (await timer.WaitForNextTickAsync(token))
                {
                    Refresh();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    // ---- 接收分发 ----

    private void OnTransportData(byte[] data)
    {
        List<byte[]> frames;
        lock (_gate)
        {
            frames = _framer.Append(data);
        }
        foreach (var packet in frames)
        {
            var frame = OppoFrame.Parse(packet);
            if (frame != null) Dispatch(frame.Value);
        }
    }

    private void Dispatch(in OppoFrame frame)
    {
        var nextBattery = OppoParsers.Battery(frame);
        if (nextBattery != null)
        {
            Battery = new BatteryStatus(
                nextBattery.Left ?? Battery.Left,
                nextBattery.Right ?? Battery.Right,
                nextBattery.ChargingCase ?? Battery.ChargingCase);
        }

        var id = OppoParsers.ProductID(frame);
        if (id != null)
        {
            ProductID = id;
            var matched = _modelRegistry.CapabilitiesFor(id);
            if (matched != null)
            {
                if (Capabilities.ModelID != matched.ModelID) DeviceEQPresets = new List<DeviceEqualizerPreset>();
                Capabilities = matched;
                DeviceName = matched.ModelName;
                if (matched.HasCustomEQ) Send(_commands.QueryAllEQ());
            }
        }

        IReadOnlyDictionary<int, NoiseControlMode> indexToMode;
        lock (_gate) indexToMode = _capabilities.NoiseModeByIndex;
        var mode = OppoParsers.NoiseControl(frame, indexToMode);
        if (mode != null) NoiseControl = mode.Value;

        var spatial = OppoParsers.SpatialAudio(frame);
        if (spatial != null) SpatialAudio = spatial.Value;

        var status = OppoParsers.FeatureStatus(frame);
        if (status != null)
        {
            if (status.GameMode != null)
            {
                _gameModeFeatureID = GameModeFeatureNew;
                GameMode = status.GameMode.Value;
            }
            else if (status.LowLatencyMode != null)
            {
                _gameModeFeatureID = GameModeFeatureLegacy;
                GameMode = status.LowLatencyMode.Value;
            }
            WearDetection = status.WearDetection ?? WearDetection;
            DualDevice = status.DualDevice ?? DualDevice;
            SpatialSound = status.SpatialSound ?? SpatialSound;
            // 型号库 spatialTypes 含 2 时静态判定为"仅三档模式",但部分固件(如 Enco X3)仍上报 0x1B 音效开关,以实机为准
            if (status.SpatialSound != null && !Capabilities.HasSpatialSound)
            {
                Capabilities = Capabilities with { HasSpatialSound = true };
            }
        }

        var preset = OppoParsers.CurrentEQ(frame);
        if (preset != null) SelectedEQ = preset.Value;

        var presets = OppoParsers.AllEqualizers(frame);
        if (presets != null)
        {
            DeviceEQPresets = presets;
            var selected = presets.FirstOrDefault(p => p.IsSelected);
            if (selected != null) SelectedEQ = selected.Id;
        }

        var codes = OppoParsers.BroadcastCodes(frame);
        if (codes != null) Send(_commands.Subscribe(codes));
    }

    // ---- 发送与断开 ----

    private void Send(byte[] data)
    {
        if (ConnectionState.Kind != ConnectionStateKind.Connected) return;
        int generation;
        lock (_gate) generation = _generation;
        _ = SendInternalAsync(data, generation);
    }

    private async Task SendInternalAsync(byte[] data, int generation)
    {
        await _sendGate.WaitAsync();
        try
        {
            await _transport.SendAsync(data);
        }
        catch (Exception error)
        {
            // 旧连接残留的排队发送在通道关闭后才会失败,不得覆盖新连接的状态
            int current;
            lock (_gate) current = _generation;
            if (current == generation)
            {
                ConnectionState = ConnectionState.FailedState(error.Message);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void OnTransportClosed()
    {
        lock (_gate)
        {
            CancelInitializationTasksLocked();
            _sessionCts?.Cancel();
            _sessionCts = null;
        }
        ResetState();
    }

    private void ResetState()
    {
        lock (_gate) _generation++;
        ConnectionState = ConnectionState.Disconnected;
        DeviceName = "未连接";
        ProductID = null;
        Battery = new BatteryStatus();
        NoiseControl = NoiseControlMode.Off;
        SpatialAudio = SpatialAudioMode.Off;
        GameMode = false;
        WearDetection = false;
        DualDevice = false;
        SpatialSound = false;
        SelectedEQ = 0;
        DeviceEQPresets = new List<DeviceEqualizerPreset>();
        lock (_gate) _gameModeFeatureID = GameModeFeatureLegacy;
        Capabilities = DeviceCapabilities.Generic;
        OnPropertyChanged(string.Empty);
    }

    private void ScheduleEqualizerRefresh()
    {
        CancellationTokenSource newCts = new();
        CancellationTokenSource? oldCts;
        lock (_gate)
        {
            oldCts = _equalizerRefreshCts;
            _equalizerRefreshCts = newCts;
        }
        oldCts?.Cancel();
        oldCts?.Dispose();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(EqualizerRefreshDebounce, newCts.Token);
                RefreshCustomEQ();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelInitializationTasksLocked()
    {
        _timeoutCts?.Cancel();
        _timeoutCts = null;
        _equalizerRefreshCts?.Cancel();
        _equalizerRefreshCts = null;
    }

    // ---- 通知 ----

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(ConnectionState)) ConnectionStateChanged?.Invoke(ConnectionState);
    }

    public void Dispose()
    {
        Disconnect();
        _transport.OnData -= OnTransportData;
        _transport.OnClosed -= OnTransportClosed;
        _transport.Dispose();
        _sendGate.Dispose();
    }
}
