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
    private bool _hasReading;
    private int _zeroStreak;
    private int _readFailures;
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
                var hrVolume = _endpoint.GetMasterVolumeLevelScalar(out var volume);
                var hrMute = _endpoint.GetMute(out var muted);
                if (hrVolume >= 0 && hrMute >= 0)
                {
                    _readFailures = 0;
                    if (ApplyReading(volume, muted))
                    {
                        Changed?.Invoke(_volume, _muted);
                    }
                }
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
            var hrVolume = _endpoint.GetMasterVolumeLevelScalar(out var volume);
            var hrMute = _endpoint.GetMute(out var muted);
            if (hrVolume < 0 || hrMute < 0)
            {
                // A transient COM failure used to surface as "volume 0". Keep the
                // last good reading; if the endpoint stays broken, rebuild it.
                if (++_readFailures >= 5)
                {
                    _readFailures = 0;
                    ReinitializeEndpoint();
                }

                return;
            }

            _readFailures = 0;
            if (ApplyReading(volume, muted))
            {
                Changed?.Invoke(_volume, _muted);
            }
        }
        catch
        {
            _endpoint = null;
            _nextRetry = DateTime.Now.AddSeconds(3);
        }
    }

    /// <summary>
    /// Stores a reading and tells whether it should be published. A COM read can
    /// succeed with 0 while the endpoint reconfigures (device switch, Bluetooth
    /// profile change); publishing that used to flash the island 0% ↔ N%. A
    /// sudden 0 is therefore only accepted once it persists.
    /// </summary>
    private bool ApplyReading(float volume, bool muted)
    {
        volume = Math.Clamp(volume, 0f, 1f);

        if (volume <= 0.001f && !muted && _hasReading && _volume > 0.02f && ++_zeroStreak < 2)
        {
            return false;
        }

        _zeroStreak = 0;

        var changed = !_hasReading ||
                      Math.Abs(volume - _volume) > 0.004f ||
                      muted != _muted;
        _volume = volume;
        _muted = muted;
        _hasReading = true;
        return changed;
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
            if (_endpoint.SetMasterVolumeLevelScalar((float)scalar, ref context) < 0)
            {
                return;
            }

            _volume = (float)scalar;
            _hasReading = true;
            _zeroStreak = 0;
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

        _zeroStreak = 0;
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
