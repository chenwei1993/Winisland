using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Threading;
using WinIsland.Interop;

namespace WinIsland.Services;

public sealed class SystemMonitor : IDisposable
{
    private const int HistoryLength = 34;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _ramHistory = new();
    private readonly Queue<double> _netHistory = new();

    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private long _previousReceived;
    private long _previousSent;
    private DateTime _previousNetworkTime = DateTime.UtcNow;
    private bool _hasCpuBaseline;

    public double CpuPercent { get; private set; }
    public double RamPercent { get; private set; }
    public string RamUsedText { get; private set; } = string.Empty;
    public double DownBytesPerSecond { get; private set; }
    public double UpBytesPerSecond { get; private set; }
    public string AdapterText { get; private set; } = "网络";
    public string DiskText { get; private set; } = string.Empty;
    public TimeSpan Uptime { get; private set; }

    public double[] CpuSamples => _cpuHistory.ToArray();
    public double[] RamSamples => _ramHistory.ToArray();
    public double[] NetSamples => _netHistory.ToArray();

    public event Action? Updated;

    public void Start()
    {
        CaptureNetworkTotals(out _previousReceived, out _previousSent, out var adapter);
        AdapterText = adapter;
        _previousNetworkTime = DateTime.UtcNow;
        Poll();
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        PollCpu();
        PollMemory();
        PollNetwork();
        RefreshDiskText();
        Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        Push(_cpuHistory, CpuPercent);
        Push(_ramHistory, RamPercent);
        Push(_netHistory, DownBytesPerSecond);

        Updated?.Invoke();
    }

    private void PollCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return;
        }

        var idleValue = idle.ToUInt64();
        var kernelValue = kernel.ToUInt64();
        var userValue = user.ToUInt64();

        if (_hasCpuBaseline)
        {
            var idleDelta = idleValue - _previousIdle;
            var kernelDelta = kernelValue - _previousKernel;
            var userDelta = userValue - _previousUser;
            var total = kernelDelta + userDelta;

            if (total > 0)
            {
                CpuPercent = Math.Clamp((1d - (double)idleDelta / total) * 100d, 0, 100);
            }
        }

        _previousIdle = idleValue;
        _previousKernel = kernelValue;
        _previousUser = userValue;
        _hasCpuBaseline = true;
    }

    private void PollMemory()
    {
        var status = Native.MEMORYSTATUSEX.Create();
        if (!Native.GlobalMemoryStatusEx(ref status))
        {
            return;
        }

        RamPercent = status.ullTotalPhys == 0
            ? 0
            : Math.Clamp((status.ullTotalPhys - status.ullAvailPhys) * 100d / status.ullTotalPhys, 0, 100);

        var used = (status.ullTotalPhys - status.ullAvailPhys) / 1024d / 1024d / 1024d;
        var total = status.ullTotalPhys / 1024d / 1024d / 1024d;
        RamUsedText = $"{used:0.0} / {total:0.0} GB";
    }

    private void PollNetwork()
    {
        var now = DateTime.UtcNow;
        CaptureNetworkTotals(out var received, out var sent, out var adapter);
        AdapterText = adapter;

        var seconds = Math.Max((now - _previousNetworkTime).TotalSeconds, 0.5);
        DownBytesPerSecond = Math.Max(0, (received - _previousReceived) / seconds);
        UpBytesPerSecond = Math.Max(0, (sent - _previousSent) / seconds);

        _previousReceived = received;
        _previousSent = sent;
        _previousNetworkTime = now;
    }

    private static void CaptureNetworkTotals(out long received, out long sent, out string adapterText)
    {
        received = 0;
        sent = 0;

        NetworkInterface? best = null;
        var bestScore = int.MinValue;
        NetworkInterface? fallback = null;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                try
                {
                    var statistics = nic.GetIPStatistics();
                    received += statistics.BytesReceived;
                    sent += statistics.BytesSent;
                }
                catch
                {
                    continue;
                }

                fallback ??= nic;

                var score = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => 4,
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 3,
                    _ => 1
                };

                var hasGateway = false;
                try
                {
                    hasGateway = nic.GetIPProperties().GatewayAddresses.Count > 0;
                }
                catch
                {
                    // Ignore.
                }

                if (hasGateway)
                {
                    score += 2;
                }

                var description = $"{nic.Name} {nic.Description}".ToLowerInvariant();
                if (description.Contains("tap") || description.Contains("tun") ||
                    description.Contains("vpn") || description.Contains("virtual") ||
                    description.Contains("loopback"))
                {
                    score -= 4;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = nic;
                }
            }

            best ??= fallback;
        }
        catch
        {
            adapterText = "网络不可用";
            return;
        }

        if (best is null)
        {
            adapterText = "未连接网络";
            return;
        }

        var type = best.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "以太网",
            _ => "网络"
        };

        string? ip = null;
        try
        {
            ip = best.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address.ToString();
        }
        catch
        {
            // Ignore.
        }

        adapterText = string.IsNullOrWhiteSpace(ip) ? type : $"{type} · {ip}";
    }

    private void RefreshDiskText()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                DiskText = $"系统盘 {FormatBytes(drive.AvailableFreeSpace)} 可用";
                return;
            }
        }
        catch
        {
            // Ignore.
        }

        DiskText = "磁盘信息不可用";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static void Push(Queue<double> queue, double value)
    {
        queue.Enqueue(double.IsFinite(value) ? value : 0);
        while (queue.Count > HistoryLength)
        {
            queue.Dequeue();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
