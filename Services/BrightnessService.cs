using System.Windows;
using System.Windows.Threading;
using WinIsland.Interop;

namespace WinIsland.Services;

public sealed class BrightnessService : IDisposable
{
    private readonly object _gate = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private System.Threading.Timer? _pollTimer;
    private Native.PHYSICAL_MONITOR[]? _monitors;
    private IntPtr _handle;
    private uint _minimum;
    private uint _maximum;
    private int _percent;
    private int _lastReported = -1;
    private bool _polling;
    private DateTime _nextRetry = DateTime.MinValue;

    public bool IsAvailable => _handle != IntPtr.Zero;
    public int Percent => _percent;

    public event Action<int>? Changed;

    public void Start()
    {
        TryInitialize();
        _uiTimer.Tick += (_, _) => TryInitialize();
        _uiTimer.Start();

        _pollTimer = new System.Threading.Timer(_ => Poll(), null, 1500, 1500);
    }

    private void TryInitialize()
    {
        if (_handle != IntPtr.Zero || DateTime.Now < _nextRetry)
        {
            return;
        }

        try
        {
            var monitor = Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTOPRIMARY);
            if (monitor == IntPtr.Zero ||
                !Native.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) ||
                count == 0)
            {
                _nextRetry = DateTime.Now.AddSeconds(10);
                return;
            }

            var monitors = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors))
            {
                _nextRetry = DateTime.Now.AddSeconds(10);
                return;
            }

            var handle = monitors[0].hPhysicalMonitor;
            if (handle == IntPtr.Zero || !Native.GetMonitorBrightness(handle, out var min, out var current, out var max) || max <= min)
            {
                Native.DestroyPhysicalMonitors(count, monitors);
                _nextRetry = DateTime.Now.AddSeconds(10);
                return;
            }

            lock (_gate)
            {
                _monitors = monitors;
                _handle = handle;
                _minimum = min;
                _maximum = max;
                _percent = (int)Math.Round((current - min) * 100d / (max - min));
                _lastReported = _percent;
            }

            _uiTimer.Stop();
            Changed?.Invoke(_percent);
        }
        catch
        {
            _nextRetry = DateTime.Now.AddSeconds(15);
        }
    }

    private void Poll()
    {
        if (Volatile.Read(ref _handle) == IntPtr.Zero || _polling)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(TryInitialize));
            return;
        }

        _polling = true;
        try
        {
            Native.GetMonitorBrightness(_handle, out _, out var current, out _);
            var percent = (int)Math.Round((current - _minimum) * 100d / (_maximum - _minimum));
            if (Math.Abs(percent - _lastReported) >= 1)
            {
                _lastReported = percent;
                _percent = percent;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke(percent)));
            }
        }
        catch
        {
            // Monitor may have gone to sleep or been unplugged.
        }
        finally
        {
            _polling = false;
        }
    }

    public void SetPercent(int percent)
    {
        if (Volatile.Read(ref _handle) == IntPtr.Zero)
        {
            return;
        }

        percent = Math.Clamp(percent, 0, 100);
        _percent = percent;
        _lastReported = percent;

        var handle = _handle;
        var min = _minimum;
        var max = _maximum;
        var target = min + (uint)Math.Round(percent / 100d * (max - min));

        Task.Run(() =>
        {
            try
            {
                Native.SetMonitorBrightness(handle, target);
            }
            catch
            {
                // Ignore DDC failures.
            }
        });

        Changed?.Invoke(percent);
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _pollTimer?.Dispose();

        lock (_gate)
        {
            if (_monitors is not null)
            {
                try
                {
                    Native.DestroyPhysicalMonitors((uint)_monitors.Length, _monitors);
                }
                catch
                {
                    // Ignore.
                }

                _monitors = null;
            }

            _handle = IntPtr.Zero;
        }
    }
}
