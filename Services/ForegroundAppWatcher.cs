using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinIsland.Interop;
using WinIsland.Models;

namespace WinIsland.Services;

/// <summary>
/// Watches the foreground window and reports app launches / app switches.
/// Uses an out-of-context WinEvent hook with a polling fallback.
/// </summary>
public sealed class ForegroundAppWatcher : IDisposable
{
    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "searchhost", "searchapp", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "lockapp", "sihost", "widgets", "widgetservice",
        "dwm", "winlogon", "csrss", "wininit", "services", "lsass", "smss",
        "fontdrvhost", "runtimebroker", "backgroundtaskhost", "dllhost",
        "msedgewebview2", "webviewhost", "openwith", "shellembeddinghost"
    };

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "ApplicationManager_DesktopShellWindow",
        "TaskSwitcherWnd", "MultitaskingViewFrame", "ForegroundStaging",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        "SysShadow", "MsgrIMEWindowClass", "IME", "MSCTFIME UI"
    };

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly HashSet<uint> _seenProcesses = new();
    private readonly Dictionary<uint, ProcessSnapshot> _processCache = new();
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Native.WinEventDelegate _hookDelegate;

    private IntPtr _hook;
    private IntPtr _lastForeground;
    private uint _lastPid;
    private DateTime _lastShown = DateTime.MinValue;
    private bool _started;
    private bool _busy;

    public event Action<AppSwitchInfo>? Changed;

    public ForegroundAppWatcher()
    {
        _hookDelegate = WinEventCallback;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // Prime with the app that is already in front, so starting WinIsland
        // doesn't immediately announce that app as "opened".
        _lastForeground = Native.GetForegroundWindow();
        if (_lastForeground != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(_lastForeground, out var pid);
            if (pid != 0)
            {
                _seenProcesses.Add(pid);
            }
        }

        try
        {
            _hook = Native.SetWinEventHook(
                Native.EVENT_SYSTEM_FOREGROUND,
                Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _hookDelegate,
                0,
                0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        }
        catch
        {
            _hook = IntPtr.Zero;
        }

        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType != Native.EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            EvaluateSafe(hwnd);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => EvaluateSafe(hwnd)));
        }
    }

    private void Poll()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == _lastForeground)
        {
            return;
        }

        _lastForeground = hwnd;
        EvaluateSafe(hwnd);
    }

    private void EvaluateSafe(IntPtr hwnd)
    {
        if (!_started || _busy)
        {
            return;
        }

        if ((DateTime.Now - _startedAt).TotalMilliseconds < 1200)
        {
            return;
        }

        _busy = true;
        try
        {
            Evaluate(hwnd);
        }
        catch (Exception exception)
        {
            Trace("evaluate failed: " + exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Evaluate(IntPtr hwnd)
    {
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root != IntPtr.Zero)
        {
            hwnd = root;
        }

        if (!Native.IsWindowVisible(hwnd) || !Native.GetWindowRect(hwnd, out var rect) ||
            rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return;
        }

        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 &&
            cloaked != 0)
        {
            return;
        }

        var className = new StringBuilder(128);
        Native.GetClassName(hwnd, className, className.Capacity);
        var cls = className.ToString();
        if (IgnoredClasses.Contains(cls))
        {
            return;
        }

        var exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0)
        {
            return;
        }

        var process = GetProcessSnapshot(pid);
        if (process is null || IgnoredProcesses.Contains(process.ProcessName))
        {
            return;
        }

        var title = ReadWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title))
        {
            if (process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("applicationframehost", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            title = process.DisplayName;
        }

        if (process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
            (title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase) ||
             title.Equals("程序管理器", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var displayName = GetDisplayName(process, title);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return;
        }

        var now = DateTime.Now;
        if (pid == _lastPid && (now - _lastShown).TotalSeconds < 1.5)
        {
            return;
        }

        var firstSighting = _seenProcesses.Add(pid);
        var newlyOpened = firstSighting && process.StartTime >= _startedAt;

        _lastPid = pid;
        _lastShown = now;

        var icon = GetIcon(hwnd, process);
        var info = new AppSwitchInfo
        {
            ProcessId = pid,
            ProcessName = process.ProcessName,
            DisplayName = displayName,
            WindowTitle = title,
            IsNewlyOpened = newlyOpened,
            Icon = icon
        };

        Trace($"{(newlyOpened ? "opened" : "switch")} {displayName} [{pid}] \"{title}\"");
        Changed?.Invoke(info);
    }

    private ProcessSnapshot? GetProcessSnapshot(uint pid)
    {
        if (_processCache.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            var processName = process.ProcessName;
            var startTime = DateTime.MinValue;
            string? executablePath = null;

            try
            {
                startTime = process.StartTime;
            }
            catch
            {
                // Protected process.
            }

            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch
            {
                // Access denied (elevated app).
            }

            string? description = null;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                try
                {
                    var version = FileVersionInfo.GetVersionInfo(executablePath);
                    description = !string.IsNullOrWhiteSpace(version.FileDescription)
                        ? version.FileDescription
                        : version.ProductName;
                }
                catch
                {
                    // Ignore.
                }
            }

            var displayName = KnownDisplayName(processName) ??
                              CleanDescription(description) ??
                              FriendlyProcessName(processName);

            var snapshot = new ProcessSnapshot
            {
                ProcessName = processName,
                DisplayName = displayName,
                ExecutablePath = executablePath,
                StartTime = startTime
            };

            _processCache[pid] = snapshot;
            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    private static string GetDisplayName(ProcessSnapshot process, string windowTitle)
    {
        if (process.ProcessName.Equals("applicationframehost", StringComparison.OrdinalIgnoreCase))
        {
            var title = windowTitle.Trim();
            if (title.Length > 32)
            {
                title = title[..32].Trim();
            }

            return string.IsNullOrWhiteSpace(title) ? "Windows 应用" : title;
        }

        return process.DisplayName;
    }

    private static string? KnownDisplayName(string processName)
    {
        return processName.ToLowerInvariant() switch
        {
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "firefox" => "Firefox",
            "code" => "Visual Studio Code",
            "devenv" => "Visual Studio",
            "rider" or "rider64" => "JetBrains Rider",
            "explorer" => "文件资源管理器",
            "notepad" => "记事本",
            "calc" or "calculator" or "calculatorapp" => "计算器",
            "mspaint" => "画图",
            "taskmgr" => "任务管理器",
            "wmplayer" => "Windows Media Player",
            "spotify" => "Spotify",
            "qqmusic" => "QQ音乐",
            "cloudmusic" => "网易云音乐",
            "kuwo" => "酷我音乐",
            "wechat" or "weixin" => "微信",
            "telegram" => "Telegram",
            "discord" => "Discord",
            "windowsterminal" or "wt" => "Windows Terminal",
            "powershell" or "pwsh" => "PowerShell",
            "cmd" => "命令提示符",
            "systemsettings" => "设置",
            "applicationframehost" => "Windows 应用",
            _ => null
        };
    }

    private static string? CleanDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var value = description.Replace("®", string.Empty).Replace("™", string.Empty).Trim();
        if (value.Length is < 2 or > 48)
        {
            return null;
        }

        if (value.Contains("Microsoft Windows", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Windows Operating System", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value;
    }

    private static string FriendlyProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return "应用";
        }

        if (processName.Length == 1)
        {
            return processName.ToUpperInvariant();
        }

        return char.ToUpperInvariant(processName[0]) + processName[1..];
    }

    private static string ReadWindowTitle(IntPtr hwnd)
    {
        var length = Native.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(length + 1, 512));
        Native.GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private ImageSource? GetIcon(IntPtr hwnd, ProcessSnapshot process)
    {
        var windowIcon = TryGetWindowIcon(hwnd);
        if (windowIcon is not null)
        {
            return windowIcon;
        }

        if (string.IsNullOrWhiteSpace(process.ExecutablePath) ||
            process.ProcessName.Equals("applicationframehost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_iconCache.TryGetValue(process.ExecutablePath, out var cached))
        {
            return cached;
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(process.ExecutablePath);
            if (icon is null)
            {
                _iconCache[process.ExecutablePath] = null;
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            _iconCache[process.ExecutablePath] = source;
            return source;
        }
        catch
        {
            _iconCache[process.ExecutablePath] = null;
            return null;
        }
    }

    private static ImageSource? TryGetWindowIcon(IntPtr hwnd)
    {
        var handle = TrySendGetIcon(hwnd, Native.ICON_BIG);
        if (handle == IntPtr.Zero)
        {
            handle = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
        }

        if (handle == IntPtr.Zero)
        {
            handle = TrySendGetIcon(hwnd, Native.ICON_SMALL);
        }

        if (handle == IntPtr.Zero)
        {
            handle = Native.GetClassLongPtr(hwnd, Native.GCLP_HICONSM);
        }

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr TrySendGetIcon(IntPtr hwnd, int iconType)
    {
        try
        {
            // SendMessageTimeout protects the island from a hung foreground app.
            Native.SendMessageTimeout(hwnd, Native.WM_GETICON, new IntPtr(iconType), IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, 100, out var result);
            return result;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("WINISLAND_DEBUG") != "1")
        {
            return;
        }

        try
        {
            var directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinIsland");
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(directory, "debug.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [apps] {message}{Environment.NewLine}");
        }
        catch
        {
            // Debug tracing only.
        }
    }

    public void Dispose()
    {
        _started = false;
        _poll.Stop();
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private sealed class ProcessSnapshot
    {
        public required string ProcessName { get; init; }
        public required string DisplayName { get; init; }
        public string? ExecutablePath { get; init; }
        public DateTime StartTime { get; init; }
    }
}
