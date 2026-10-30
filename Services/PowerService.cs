using System.Windows.Threading;
using WinIsland.Interop;

namespace WinIsland.Services;

public sealed class PowerService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _firstPoll = true;
    private bool _hasBattery;
    private bool _onAc;
    private bool _isCharging;
    private int _percent = 100;
    private int _lastPercent = 100;
    private bool _low20Notified;
    private bool _low10Notified;

    public int Percent => _percent;
    public bool IsCharging => _onAc;
    public bool HasBattery => _hasBattery;
    public string StateText { get; private set; } = "已接通电源";
    public string TimeText { get; private set; } = string.Empty;

    public event Action? Updated;
    public event Action<bool>? AcChanged;
    public event Action<int>? LowBattery;

    public void Start()
    {
        Poll();
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        if (!Native.GetSystemPowerStatus(out var status))
        {
            return;
        }

        var percent = status.BatteryLifePercent == 255 ? 100 : status.BatteryLifePercent;
        var onAc = status.ACLineStatus == 1;
        var hasBattery = status.BatteryFlag != 128 && status.BatteryLifePercent != 255;
        var charging = (status.BatteryFlag & 8) != 0 || (onAc && hasBattery && percent < 100);
        var wasOnAc = _onAc;

        var changed = _firstPoll || percent != _percent || onAc != _onAc || charging != _isCharging || hasBattery != _hasBattery;

        _hasBattery = hasBattery;
        _onAc = onAc;
        _isCharging = charging;
        _percent = percent;

        if (hasBattery)
        {
            if (charging)
            {
                StateText = percent >= 100 ? "已充满" : "正在充电";
            }
            else if (onAc)
            {
                StateText = "已接通电源";
            }
            else
            {
                StateText = "使用电池";
            }

            if (!onAc && status.BatteryLifeTime > 0 && status.BatteryLifeTime != -1)
            {
                var remaining = TimeSpan.FromSeconds(status.BatteryLifeTime);
                TimeText = $"约 {remaining.Hours} 小时 {remaining.Minutes} 分钟";
            }
            else
            {
                TimeText = string.Empty;
            }
        }
        else
        {
            StateText = "台式机电源";
            TimeText = string.Empty;
        }

        if (!_firstPoll && onAc != wasOnAc)
        {
            AcChanged?.Invoke(onAc);
        }

        if (!_firstPoll && !onAc && hasBattery)
        {
            if (percent <= 10 && !_low10Notified)
            {
                _low10Notified = true;
                LowBattery?.Invoke(percent);
            }
            else if (percent <= 20 && !_low20Notified)
            {
                _low20Notified = true;
                LowBattery?.Invoke(percent);
            }
        }

        if (onAc || !hasBattery)
        {
            _low20Notified = false;
            _low10Notified = false;
        }

        if (changed || _firstPoll)
        {
            Updated?.Invoke();
        }

        _lastPercent = percent;
        _firstPoll = false;
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
