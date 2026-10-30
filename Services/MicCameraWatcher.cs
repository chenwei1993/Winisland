using System.Windows.Threading;
using Microsoft.Win32;
using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>
/// Watches the Windows privacy consent store (the data behind the taskbar
/// microphone/camera indicators) plus the Caps Lock key.
/// </summary>
public sealed class MicCameraWatcher : IDisposable
{
    private const string ConsentRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _mic;
    private bool _camera;
    private bool _caps;

    public bool MicrophoneInUse => _mic;
    public bool CameraInUse => _camera;
    public bool CapsLockOn => _caps;

    public event Action? Changed;

    public void Start()
    {
        Poll();
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        var mic = IsCapabilityInUse("microphone");
        var camera = IsCapabilityInUse("camera");
        var caps = (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0;

        if (mic != _mic || camera != _camera || caps != _caps)
        {
            _mic = mic;
            _camera = camera;
            _caps = caps;
            Changed?.Invoke();
        }
    }

    private static bool IsCapabilityInUse(string capability)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"{ConsentRoot}\{capability}");
            if (root is null)
            {
                return false;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var nonPackaged = root.OpenSubKey(name);
                    if (nonPackaged is null)
                    {
                        continue;
                    }

                    foreach (var appName in nonPackaged.GetSubKeyNames())
                    {
                        using var app = nonPackaged.OpenSubKey(appName);
                        if (IsActive(app))
                        {
                            return true;
                        }
                    }
                }
                else
                {
                    using var app = root.OpenSubKey(name);
                    if (IsActive(app))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Ignore registry access failures.
        }

        return false;
    }

    private static bool IsActive(RegistryKey? key)
    {
        if (key is null)
        {
            return false;
        }

        try
        {
            var start = key.GetValue("LastUsedTimeStart");
            var stop = key.GetValue("LastUsedTimeStop");
            if (start is long startTicks && stop is long stopTicks)
            {
                return stopTicks == 0 && startTicks != 0;
            }
        }
        catch
        {
            // Ignore.
        }

        return false;
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
