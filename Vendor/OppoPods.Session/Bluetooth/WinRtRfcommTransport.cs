using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace OppoPods.Session.Bluetooth;

/// <summary>
/// WinRT 实现的耳机控制通道:
/// Uncached SDP 查询两个 HeyMelody 服务 UUID → StreamSocket 连接 → 读写循环。
/// 连接前要求耳机已在系统蓝牙中连接(与 macOS 版前提一致)。
/// </summary>
public sealed class WinRtRfcommTransport : IRfcommTransport
{
    /// <summary>HeyMelody SPP 服务 UUID(macOS 版优先查询的两个)。</summary>
    public static readonly Guid[] ServiceUuids =
    {
        new("00001107-d102-11e1-9b23-00025b00a5a5"),
        new("0000079a-d102-11e1-9b23-00025b00a5a5"),
    };

    private const int ServiceLookupAttempts = 3;
    private static readonly TimeSpan[] ServiceLookupBackoff = { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1) };
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private const uint ReadChunkSize = 512;

    private readonly object _gate = new();
    private BluetoothDevice? _device;
    private StreamSocket? _socket;
    private DataWriter? _writer;
    private CancellationTokenSource? _readCts;
    private volatile bool _closedByLocal;

    public event Action<byte[]>? OnData;
    public event Action? OnClosed;

    public async Task ConnectAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
    {
        CloseInternal(notify: false, disposeDevice: true);
        _closedByLocal = false;

        var device = await BluetoothDevice.FromBluetoothAddressAsync(bluetoothAddress)
            ?? throw new TransportException("无法访问所选蓝牙设备。");
        _device = device;

        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            throw new TransportException("请先在系统蓝牙设置中连接耳机,再连接控制。");
        }

        var service = await FindServiceWithRetryAsync(device, cancellationToken)
            ?? throw new TransportException(BuildServiceNotFoundMessage(device));

        await RequestAccessQuietlyAsync(service);

        var socket = new StreamSocket();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TransportException("打开耳机控制通道超时,请稍后重试。");
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new TransportException($"打开耳机控制通道失败({ex.HResult:X8})。");
        }

        lock (_gate)
        {
            _socket = socket;
            _writer = new DataWriter(socket.OutputStream);
            _readCts = new CancellationTokenSource();
        }
        device.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;
        _ = ReadLoopAsync(socket, _readCts!.Token);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data)
    {
        DataWriter? writer;
        lock (_gate)
        {
            writer = _writer;
        }
        if (writer == null) throw new TransportException("控制通道未连接。");
        try
        {
            writer.WriteBytes(data.Span.ToArray());
            await writer.StoreAsync().AsTask();
            await writer.FlushAsync().AsTask();
        }
        catch (Exception ex)
        {
            throw new TransportException($"向耳机发送指令失败({ex.HResult:X8})。");
        }
    }

    public void Close()
    {
        _closedByLocal = true;
        CloseInternal(notify: false, disposeDevice: true);
    }

    public void Dispose() => Close();

    private static async Task<RfcommDeviceService?> FindServiceWithRetryAsync(
        BluetoothDevice device, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ServiceLookupAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(ServiceLookupBackoff[attempt - 1], cancellationToken);
                // 释放旧实例的底层 COM 引用后重建,规避系统对空结果的缓存
                device = await BluetoothDevice.FromBluetoothAddressAsync(device.BluetoothAddress)
                    ?? throw new TransportException("无法访问所选蓝牙设备。");
            }

            var result = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            var service = result.Services.FirstOrDefault(s => ServiceUuids.Contains(s.ServiceId.Uuid));
            if (service != null) return service;
        }
        return null;
    }

    private static async Task RequestAccessQuietlyAsync(RfcommDeviceService service)
    {
        try
        {
            var status = await service.RequestAccessAsync();
            if (status is Windows.Devices.Enumeration.DeviceAccessStatus.DeniedBySystem
                or Windows.Devices.Enumeration.DeviceAccessStatus.DeniedByUser)
            {
                throw new TransportException("系统拒绝访问耳机控制服务。");
            }
        }
        catch (TransportException)
        {
            throw;
        }
        catch
        {
            // unpackaged 应用可能不支持 RequestAccessAsync,忽略后直接尝试连接
        }
    }

    private static string BuildServiceNotFoundMessage(BluetoothDevice device)
    {
        var known = string.Join(", ", ServiceUuids);
        return $"没有找到 OPPO/HeyMelody 控制服务。耳机 SDP 未广播已知 UUID({known}),需实机确认是否支持 channel 15 兜底。";
    }

    private void OnDeviceConnectionStatusChanged(BluetoothDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            CloseInternal(notify: true, disposeDevice: false);
        }
    }

    private async Task ReadLoopAsync(StreamSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new DataReader(socket.InputStream)
            {
                InputStreamOptions = InputStreamOptions.Partial,
            };
            while (true)
            {
                var loaded = await reader.LoadAsync(ReadChunkSize).AsTask(cancellationToken);
                if (loaded == 0) break;
                var buffer = new byte[loaded];
                reader.ReadBytes(buffer);
                OnData?.Invoke(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            return; // 本端关闭,不通知
        }
        catch
        {
            // 读取异常视为断开
        }
        CloseInternal(notify: !_closedByLocal, disposeDevice: false);
    }

    private void CloseInternal(bool notify, bool disposeDevice)
    {
        CancellationTokenSource? readCts;
        lock (_gate)
        {
            readCts = _readCts;
            _readCts = null;
            _writer?.Dispose();
            _writer = null;
            _socket?.Dispose();
            _socket = null;
        }
        if (_device != null)
        {
            _device.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
            if (disposeDevice) _device = null;
        }
        readCts?.Cancel();
        readCts?.Dispose();

        if (notify) OnClosed?.Invoke();
    }
}
