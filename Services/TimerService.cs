using System.Diagnostics;
using System.Windows.Threading;

namespace WinIsland.Services;

public sealed class TimerService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _stopwatch = new();
    private TimeSpan _stopwatchBase;
    private DateTime _deadline;
    private TimeSpan _pausedRemaining;
    private bool _completed;

    public bool CountdownActive { get; private set; }
    public bool CountdownPaused { get; private set; }
    public TimeSpan CountdownTotal { get; private set; }
    public TimeSpan CountdownRemaining { get; private set; }
    public string CountdownLabel { get; private set; } = "计时";

    public double CountdownProgress => CountdownTotal.TotalSeconds <= 0
        ? 0
        : Math.Clamp(CountdownRemaining.TotalSeconds / CountdownTotal.TotalSeconds, 0, 1);

    public bool StopwatchRunning => _stopwatch.IsRunning;
    public TimeSpan StopwatchElapsed => _stopwatchBase + (_stopwatch.IsRunning ? _stopwatch.Elapsed : TimeSpan.Zero);

    public event Action? Updated;
    public event Action? CountdownCompleted;

    public TimerService()
    {
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        // The timer is started on demand.
    }

    public void StartCountdown(TimeSpan total, string label = "计时")
    {
        if (total <= TimeSpan.Zero)
        {
            return;
        }

        CountdownTotal = total;
        CountdownRemaining = total;
        CountdownLabel = string.IsNullOrWhiteSpace(label) ? "计时" : label;
        CountdownActive = true;
        CountdownPaused = false;
        _completed = false;
        _deadline = DateTime.Now + total;
        _timer.Start();
        Updated?.Invoke();
    }

    public void PauseCountdown()
    {
        if (!CountdownActive || CountdownPaused)
        {
            return;
        }

        _pausedRemaining = CountdownRemaining;
        CountdownPaused = true;
        if (!StopwatchRunning)
        {
            _timer.Stop();
        }

        Updated?.Invoke();
    }

    public void ResumeCountdown()
    {
        if (!CountdownActive || !CountdownPaused)
        {
            return;
        }

        _deadline = DateTime.Now + _pausedRemaining;
        CountdownPaused = false;
        _timer.Start();
        Updated?.Invoke();
    }

    public void StopCountdown()
    {
        CountdownActive = false;
        CountdownPaused = false;
        CountdownRemaining = TimeSpan.Zero;
        CountdownTotal = TimeSpan.Zero;
        _completed = false;
        if (!StopwatchRunning)
        {
            _timer.Stop();
        }

        Updated?.Invoke();
    }

    public void AddMinute()
    {
        if (!CountdownActive)
        {
            StartCountdown(TimeSpan.FromMinutes(1), CountdownLabel);
            return;
        }

        var extra = TimeSpan.FromMinutes(1);
        CountdownTotal += extra;
        CountdownRemaining += extra;
        if (CountdownPaused)
        {
            _pausedRemaining += extra;
        }
        else
        {
            _deadline += extra;
        }

        Updated?.Invoke();
    }

    private void Tick()
    {
        if (CountdownActive && !CountdownPaused)
        {
            var remaining = _deadline - DateTime.Now;
            if (remaining <= TimeSpan.Zero)
            {
                CountdownRemaining = TimeSpan.Zero;
                CountdownActive = false;
                CountdownPaused = false;

                if (!StopwatchRunning)
                {
                    _timer.Stop();
                }

                if (!_completed)
                {
                    _completed = true;
                    CountdownCompleted?.Invoke();
                }
            }
            else
            {
                CountdownRemaining = remaining;
            }
        }

        if (!CountdownActive && !StopwatchRunning)
        {
            _timer.Stop();
        }

        Updated?.Invoke();
    }

    public void StartStopwatch()
    {
        if (_stopwatch.IsRunning)
        {
            return;
        }

        _stopwatch.Restart();
        _timer.Start();
        Updated?.Invoke();
    }

    public void PauseStopwatch()
    {
        if (!_stopwatch.IsRunning)
        {
            return;
        }

        _stopwatchBase += _stopwatch.Elapsed;
        _stopwatch.Reset();
        if (!CountdownActive)
        {
            _timer.Stop();
        }

        Updated?.Invoke();
    }

    public void ResetStopwatch()
    {
        _stopwatch.Reset();
        _stopwatchBase = TimeSpan.Zero;
        if (!CountdownActive)
        {
            _timer.Stop();
        }

        Updated?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
