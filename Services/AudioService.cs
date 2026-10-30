using System.Windows.Threading;
using WinIsland.Interop;

namespace WinIsland.Services;

public sealed class AudioService : IDisposable
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _devicePoll = new() { Interval = TimeSpan.FromSeconds(2) };
    private CoreAudio.IAudioEndpointVolume? _endpoint;
    private DateTime _nextRetry = DateTime.MinValue;
    private bool _muted;
    private float _volume;
    private bool _initialized;
    private string _deviceId = string.Empty;

    public bool IsAvailable => _endpoint is not null;
    public float Volume => _volume;
    public bool IsMuted => _muted;

    public event Action<float, bool>? Changed;
    public event Action? DevicesChanged;

    public void Start()
    {
        TryInitialize();
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        _devicePoll.Tick += (_, _) => PollDefaultDevice();
        _devicePoll.Start();
    }

    internal IReadOnlyList<CoreAudio.AudioEndpoint> GetOutputDevices()
    {
        return CoreAudio.GetRenderEndpoints();
    }

    internal bool SetDefaultDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        if (string.Equals(deviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!CoreAudio.SetDefaultRenderDevice(deviceId))
        {
            return false;
        }

        _deviceId = deviceId;
        ReinitializeEndpoint();
        DevicesChanged?.Invoke();
        return true;
    }

    private void TryInitialize()
    {
        if (_endpoint is not null || DateTime.Now < _nextRetry)
        {
            return;
        }

        try
        {
            _endpoint = CoreAudio.CreateEndpointVolume();
            if (_endpoint is not null)
            {
                _deviceId = CoreAudio.GetDefaultRenderDeviceId() ?? string.Empty;
                _endpoint.GetMasterVolumeLevelScalar(out _volume);
                _endpoint.GetMute(out _muted);
                _initialized = true;
                Changed?.Invoke(_volume, _muted);
            }
        }
        catch
        {
            _endpoint = null;
        }

        if (_endpoint is null)
        {
            _nextRetry = DateTime.Now.AddSeconds(5);
        }
    }

    private void Poll()
    {
        if (_endpoint is null)
        {
            TryInitialize();
            return;
        }

        try
        {
            _endpoint.GetMasterVolumeLevelScalar(out var volume);
            _endpoint.GetMute(out var muted);

            if (!_initialized || Math.Abs(volume - _volume) > 0.004f || muted != _muted)
            {
                var changed = !_initialized || Math.Abs(volume - _volume) > 0.004f || muted != _muted;
                _volume = Math.Clamp(volume, 0f, 1f);
                _muted = muted;
                _initialized = true;
                if (changed)
                {
                    Changed?.Invoke(_volume, _muted);
                }
            }
        }
        catch
        {
            _endpoint = null;
            _nextRetry = DateTime.Now.AddSeconds(3);
        }
    }

    public void SetVolume(double scalar)
    {
        if (_endpoint is null)
        {
            return;
        }

        scalar = Math.Clamp(scalar, 0, 1);
        try
        {
            var context = Guid.Empty;
            _endpoint.SetMasterVolumeLevelScalar((float)scalar, ref context);
            _volume = (float)scalar;
            if (_muted)
            {
                _endpoint.SetMute(false, ref context);
                _muted = false;
            }

            Changed?.Invoke(_volume, _muted);
        }
        catch
        {
            // Ignore endpoint failures.
        }
    }

    public void ToggleMute()
    {
        if (_endpoint is null)
        {
            return;
        }

        try
        {
            var context = Guid.Empty;
            _endpoint.SetMute(!_muted, ref context);
            _muted = !_muted;
            Changed?.Invoke(_volume, _muted);
        }
        catch
        {
            // Ignore endpoint failures.
        }
    }

    private void PollDefaultDevice()
    {
        var id = CoreAudio.GetDefaultRenderDeviceId();
        if (string.IsNullOrEmpty(_deviceId) ||
            string.IsNullOrEmpty(id) ||
            string.Equals(id, _deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _deviceId = id;
        ReinitializeEndpoint();
        DevicesChanged?.Invoke();
    }

    private void ReinitializeEndpoint()
    {
        if (_endpoint is not null)
        {
            try
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(_endpoint);
            }
            catch
            {
                // Ignore.
            }

            _endpoint = null;
        }

        _initialized = false;
        _nextRetry = DateTime.MinValue;
        TryInitialize();
    }

    public void Dispose()
    {
        _poll.Stop();
        _devicePoll.Stop();
        if (_endpoint is not null)
        {
            try
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(_endpoint);
            }
            catch
            {
                // Ignore.
            }

            _endpoint = null;
        }
    }
}
