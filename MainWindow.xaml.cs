using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinIsland.Interop;
using WinIsland.Models;
using WinIsland.Panes;
using WinIsland.Services;

namespace WinIsland;

public partial class MainWindow : Window
{
    private const double IdleWidth = 272;    private const double IdleHeight = 40;
    private const double CompactWidth = 424;
    private const double CompactHeight = 56;
    private const double ExpandedWidth = 600;
    private const double ExpandedHeight = 520;
    private const double DropWidth = 420;
    private const double DropHeight = 120;
    private const double SliverWidth = 120;
    private const double SliverHeight = 4;

    private static readonly HashSet<string> NonAppWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", "ApplicationManager_DesktopShellWindow",
        "TaskSwitcherWnd", "MultitaskingViewFrame", "ForegroundStaging",
        "XamlExplorerHostIslandWindow", "SysShadow", "MsgrIMEWindowClass", "IME",
        "MSCTFIME UI", "tooltips_class32", "#32768"
    };

    private enum Surface
    {
        Idle,
        Compact,
        Expanded,
        Drop,
        Sliver
    }

    private readonly ServiceHub _s = App.Services;
    private readonly DispatcherTimer _presentTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly GlobalMouseHook _mouseHook = new();

    private Surface _surface = Surface.Idle;
    private LiveActivity? _currentActivity;
    private LiveActivity? _transient;
    private DateTime _transientUntil;
    private DateTime _mediaHoldUntil = DateTime.MinValue;
    private bool _lastMediaPlaying;
    private DateTime _lastAppSwitchShown = DateTime.MinValue;
    private bool _fullscreenHidden;
    private bool _initialized;
    private bool _compactSeeking;
    private double _compactSeekFraction;
    private bool _edgeMode;
    private bool _smartMode;
    private double _topInset = 8;
    private DateTime _edgeHideAt = DateTime.MinValue;
    private DateTime _edgeHoverSince = DateTime.MinValue;
    private DateTime _smartHideAt = DateTime.MinValue;
    private DateTime _smartRevealAt = DateTime.MinValue;
    private DateTime _suppressDeactivateUntil = DateTime.MinValue;
    private DateTime _lastMemoryTrim = DateTime.MinValue;
    private UserControl? _activePane;
    private int _activePaneIndex = -1;
    private HwndSource? _source;
    private IntPtr _hwnd;

    private DateTime _animationStart;
    private double _fromIslandWidth;
    private double _fromIslandHeight;
    private double _fromIslandRadius;
    private double _toIslandWidth;
    private double _toIslandHeight;
    private double _toIslandRadius;
    private int _animationMs;
    private int _animationId;
    private bool _isAnimating;
    private Action? _animationDone;

    public MainViewModel Vm { get; }

    public MainWindow()
    {
        InitializeComponent();

        Vm = new MainViewModel(_s);
        DataContext = Vm;

        Topmost = _s.Settings.Current.AlwaysOnTop;
        _edgeMode = _s.Settings.Current.EdgeAutoHide;
        _smartMode = !_edgeMode && _s.Settings.Current.SmartAvoidWindows;
        _topInset = _edgeMode || _smartMode ? 0 : 8;
        SetWindowSize(_edgeMode ? SliverWidth : IdleWidth, _edgeMode ? SliverHeight : IdleHeight);
        SetIslandSize(_edgeMode ? SliverWidth : IdleWidth, _edgeMode ? SliverHeight : IdleHeight,
            _edgeMode ? SliverHeight / 2 : IdleHeight / 2);

        if (_edgeMode)
        {
            _surface = Surface.Sliver;
            IdleFace.Visibility = Visibility.Collapsed;
            IdleFace.Opacity = 0;
            SliverFace.Visibility = Visibility.Visible;
            SliverFace.Opacity = 1;
            SetInteraction(Surface.Sliver);
        }

        _s.Media.Updated += OnMediaUpdated;
        _s.Audio.Changed += OnAudioChanged;
        _s.Brightness.Changed += OnBrightnessChanged;
        _s.Power.AcChanged += OnAcChanged;
        _s.Power.LowBattery += OnLowBattery;
        _s.Clipboard.ItemAdded += OnClipboardItemAdded;
        _s.Timers.Updated += OnTimersUpdated;
        _s.Timers.CountdownCompleted += OnCountdownCompleted;
        _s.Apps.Changed += OnForegroundAppChanged;
        _s.SystemNotifications.Received += OnSystemNotification;

        _presentTimer.Tick += (_, _) => OnPresentTimer();
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        _edgeTimer.Tick += (_, _) => EdgeTick();
        _mouseHook.Clicked += OnGlobalClick;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => RepositionWindow();
        Closed += OnClosed;
    }

    // Lifecycle
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        RepositionWindow();
        SetInteraction(_surface);

        _presentTimer.Start();
        _fullscreenTimer.Start();

        if (_surface == Surface.Sliver)
        {
            SliverFace.Visibility = Visibility.Visible;
        }
        else
        {
            IdleFace.Visibility = Visibility.Visible;
        }

        _edgeTimer.Start();

        Nav.SelectedIndex = Vm.HasMedia ? 0 : 3;

        if (App.PendingCommand is { } pending)
        {
            App.PendingCommand = null;
            var command = IslandCommand.Parse(pending);
            if (command is not null)
            {
                Dispatcher.BeginInvoke(new Action(() => HandleCommand(command)));
            }
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        _s.Clipboard.Attach(_hwnd);
        Native.RegisterHotKey(_hwnd, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_I);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            _s.Clipboard.OnClipboardUpdate();
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == 1)
        {
            ToggleFromTray();
            handled = true;
        }
        else if (msg == 0x007E) // WM_DISPLAYCHANGE
        {
            Dispatcher.BeginInvoke(new Action(RepositionWindow));
        }

        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _presentTimer.Stop();
        _fullscreenTimer.Stop();
        _edgeTimer.Stop();
        CompositionTarget.Rendering -= OnRendering;
        _mouseHook.Dispose();

        if (_hwnd != IntPtr.Zero)
        {
            Native.UnregisterHotKey(_hwnd, 1);
        }

        _source?.RemoveHook(WndProc);
        _source = null;

        _s.Media.Updated -= OnMediaUpdated;
        _s.Audio.Changed -= OnAudioChanged;
        _s.Brightness.Changed -= OnBrightnessChanged;
        _s.Power.AcChanged -= OnAcChanged;
        _s.Power.LowBattery -= OnLowBattery;
        _s.Clipboard.ItemAdded -= OnClipboardItemAdded;
        _s.Timers.Updated -= OnTimersUpdated;
        _s.Timers.CountdownCompleted -= OnCountdownCompleted;
        _s.Apps.Changed -= OnForegroundAppChanged;
        _s.SystemNotifications.Received -= OnSystemNotification;

        Vm.Dispose();
    }

    // Public surface used by the tray icon / panes
    public void ToggleFromTray()
    {
        if (_fullscreenHidden)
        {
            RestoreFromFullscreen();
        }

        if (_surface == Surface.Expanded)
        {
            Collapse();
        }
        else
        {
            Expand();
        }
    }

    public void ShowSettings() => Expand(7);

    public void ShowTestNotification()
    {
        PushAlert("灵动岛已就绪", "这是一条来自 WinIsland 的测试通知。", "WinIsland", "success");
    }

    public void ClearAlerts() => Vm.ClearAlerts();

    public void RemoveAlert(IslandAlert alert) => Vm.RemoveAlert(alert);

    public void ApplyTopmost(bool topmost) => Topmost = topmost;

    public void ApplyFullscreenSetting()
    {
        if (!_s.Settings.Current.HideInFullscreen && _fullscreenHidden)
        {
            RestoreFromFullscreen();
        }
    }

    public void ApplyVisibilityMode()
    {
        var edge = _s.Settings.Current.EdgeAutoHide;
        var smart = !edge && _s.Settings.Current.SmartAvoidWindows;

        _edgeMode = edge;
        _smartMode = smart;
        _topInset = edge || smart ? 0 : 8;
        _edgeHideAt = DateTime.MinValue;
        _edgeHoverSince = DateTime.MinValue;
        _smartHideAt = DateTime.MinValue;
        _smartRevealAt = DateTime.MinValue;

        RepositionWindow();

        if (_surface is Surface.Expanded or Surface.Drop)
        {
            SetWindowSizeAtomic(Width, Height);
            return;
        }

        if (edge)
        {
            if (_surface != Surface.Sliver)
            {
                ShowSliver();
            }

            return;
        }

        if (smart && _surface == Surface.Sliver && IsIslandAreaCovered())
        {
            return;
        }

        if (_surface == Surface.Sliver)
        {
            var activity = ResolveActivity();
            if (activity is null)
            {
                ShowIdle(force: true);
            }
            else
            {
                ShowCompact(activity);
            }
        }
        else
        {
            SetWindowSizeAtomic(Width, Height);
        }
    }

    public void HandleCommand(IslandCommand command)
    {
        switch (command.Action)
        {
            case IslandCommandAction.Show:
                Expand();
                break;

            case IslandCommandAction.Collapse:
                Collapse();
                break;

            case IslandCommandAction.Timer:
            {
                var minutes = Math.Clamp(command.Minutes, 0.1, 600);
                var label = string.IsNullOrWhiteSpace(command.Title) ? "计时" : command.Title;
                _s.Timers.StartCountdown(TimeSpan.FromMinutes(minutes), label);
                if (_surface == Surface.Expanded)
                {
                    SelectPane(1);
                }
                else if (_surface == Surface.Sliver)
                {
                    ShowTransient(BuildTimerActivity(), 4);
                }
                else
                {
                    Present();
                }

                break;
            }

            case IslandCommandAction.Shelf:
            {
                var added = _s.Shelf.Add(command.Files);
                if (added <= 0)
                {
                    break;
                }

                if (_surface == Surface.Expanded)
                {
                    SelectPane(5);
                }
                else
                {
                    ShowTransient(BuildShelfActivity(added), 3.5);
                }

                break;
            }

            case IslandCommandAction.Progress:
            {
                var progress = Math.Clamp(command.Progress, 0, 1);
                var title = string.IsNullOrWhiteSpace(command.Title) ? "进度" : command.Title;
                var message = command.Message;
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = $"{progress * 100:0}%";
                }
                else if (progress > 0)
                {
                    message = $"{message} · {progress * 100:0}%";
                }

                ShowTransient(new LiveActivity
                {
                    Kind = ActivityKind.Info,
                    Glyph = "\uE896",
                    Title = title,
                    Subtitle = message,
                    Progress = progress,
                    ShowProgress = progress > 0,
                    Accent = AccentBrush
                }, Math.Clamp(command.Seconds, 1.5, 30));

                break;
            }

            default:
                PushAlert(
                    string.IsNullOrWhiteSpace(command.Title) ? "通知" : command.Title,
                    command.Message,
                    string.IsNullOrWhiteSpace(command.App) ? "外部应用" : command.App,
                    command.Kind,
                    bannerSeconds: Math.Clamp(command.Seconds, 1.5, 30));
                break;
        }
    }

    // Window sizing & animation
    private void SetWindowSize(double width, double height)
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Left = workArea.Left + (workArea.Width - Width) / 2d;
        Top = workArea.Top + _topInset;
    }

    private void SetIslandSize(double width, double height, double radius)
    {
        IslandBorder.Width = Math.Max(1, width);
        IslandBorder.Height = Math.Max(1, height);
        IslandBorder.CornerRadius = new CornerRadius(Math.Max(1, radius));
    }

    private void SetWindowSizeAtomic(double width, double height)
    {
        if (_hwnd == IntPtr.Zero || Width < 1 || Height < 1 ||
            !Native.GetWindowRect(_hwnd, out var current) || current.Width <= 0 || current.Height <= 0)
        {
            SetWindowSize(width, height);
            return;
        }

        var scaleX = current.Width / Width;
        var scaleY = current.Height / Height;
        if (!double.IsFinite(scaleX) || scaleX <= 0)
        {
            scaleX = 1;
        }

        if (!double.IsFinite(scaleY) || scaleY <= 0)
        {
            scaleY = 1;
        }

        var pixelWidth = Math.Max(1, (int)Math.Round(width * scaleX));
        var pixelHeight = Math.Max(1, (int)Math.Round(height * scaleY));
        var targetLeft = (int)Math.Round(current.Left + current.Width / 2d - pixelWidth / 2d);

        // Move and resize in one Win32 operation. Changing Window.Width before
        // Window.Left makes the centered island visibly jump to one side for a frame.
        Native.SetWindowPos(_hwnd, IntPtr.Zero, targetLeft, current.Top, pixelWidth, pixelHeight,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - width) / 2d;
        Top = workArea.Top + _topInset;
        Width = width;
        Height = height;
    }

    private void RepositionWindow()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2d;
        Top = workArea.Top + _topInset;
    }

    private void AnimateTo(double contentWidth, double contentHeight, double radius, int milliseconds,
        Action? completed = null)
    {
        var id = ++_animationId;

        // Grow the transparent host window once, up front. The island itself then
        // animates inside a fixed-size window, which avoids resizing the layered
        // window every frame (the main source of jank).
        if (contentWidth > Width + 0.5 || contentHeight > Height + 0.5)
        {
            SetWindowSizeAtomic(Math.Max(Width, contentWidth), Math.Max(Height, contentHeight));
        }

        if (milliseconds <= 0)
        {
            SetIslandSize(contentWidth, contentHeight, radius);
            if (contentWidth < Width - 0.5 || contentHeight < Height - 0.5)
            {
                SetWindowSizeAtomic(contentWidth, contentHeight);
            }

            completed?.Invoke();
            return;
        }

        if (!_isAnimating)
        {
            // Only hook the render loop while a resize animation is running.
            // Keeping it attached at all times burns CPU and keeps the render
            // thread (and its buffers) resident while the island is a sliver.
            CompositionTarget.Rendering += OnRendering;
        }

        _isAnimating = true;
        _fromIslandWidth = IslandBorder.Width;
        _fromIslandHeight = IslandBorder.Height;
        _fromIslandRadius = IslandBorder.CornerRadius.TopLeft;
        _toIslandWidth = contentWidth;
        _toIslandHeight = contentHeight;
        _toIslandRadius = radius;
        _animationMs = milliseconds;
        _animationStart = DateTime.UtcNow;
        _animationDone = () =>
        {
            if (id == _animationId)
            {
                completed?.Invoke();
            }
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_isAnimating)
        {
            return;
        }

        var total = Math.Max(1, _animationMs);
        var progress = Math.Clamp((DateTime.UtcNow - _animationStart).TotalMilliseconds / total, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);

        SetIslandSize(
            _fromIslandWidth + (_toIslandWidth - _fromIslandWidth) * eased,
            _fromIslandHeight + (_toIslandHeight - _fromIslandHeight) * eased,
            _fromIslandRadius + (_toIslandRadius - _fromIslandRadius) * eased);

        if (progress < 1)
        {
            return;
        }

        SetIslandSize(_toIslandWidth, _toIslandHeight, _toIslandRadius);

        if (_toIslandWidth < Width - 0.5 || _toIslandHeight < Height - 0.5)
        {
            SetWindowSizeAtomic(_toIslandWidth, _toIslandHeight);
        }

        _isAnimating = false;
        CompositionTarget.Rendering -= OnRendering;
        var done = _animationDone;
        _animationDone = null;
        done?.Invoke();
    }

    private static void SetOpacity(UIElement element, double value)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = value;
    }

    private static void Fade(UIElement element, double to, int milliseconds, int delay = 0)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void HideFace(FrameworkElement face)
    {
        face.BeginAnimation(UIElement.OpacityProperty, null);
        face.Opacity = 0;
        face.Visibility = Visibility.Collapsed;
        face.IsHitTestVisible = false;
    }

    private void SetInteraction(Surface surface)
    {
        IdleFace.IsHitTestVisible = surface == Surface.Idle;
        CompactFace.IsHitTestVisible = surface == Surface.Compact;
        ExpandedFace.IsHitTestVisible = surface == Surface.Expanded;
        DropFace.IsHitTestVisible = surface == Surface.Drop;
    }

    // Surface state machine
    public void Expand(int? pane = null)
    {
        if (!IsVisible)
        {
            Show();
            RepositionWindow();
        }

        HideSliverFace();

        DebugLog("Expand(" + (pane?.ToString() ?? "null") + ")\n" + Environment.StackTrace);
        if (_fullscreenHidden)
        {
            RestoreFromFullscreen();
        }

        if (_surface == Surface.Expanded)
        {
            if (pane is int current)
            {
                SelectPane(current);
            }

            Activate();
            return;
        }

        _surface = Surface.Expanded;
        SetInteraction(Surface.Expanded);

        if (pane is int targetPane)
        {
            SelectPane(targetPane);
        }
        else if (Nav.SelectedIndex < 0)
        {
            SelectPane(Vm.HasMedia ? 0 : 3);
        }
        else
        {
            ShowPane(Nav.SelectedIndex);
        }

        DropFace.Visibility = Visibility.Collapsed;
        ExpandedFace.Visibility = Visibility.Visible;
        SetOpacity(ExpandedFace, 0.25);
        Fade(ExpandedFace, 1, 160, 0);
        Fade(CompactFace, 0, 90);
        Fade(IdleFace, 0, 90);
        Fade(DropFace, 0, 90);

        var id = ++_animationId;
        AnimateTo(ExpandedWidth, ExpandedHeight, 28, 320, () =>
        {
            if (id != _animationId || _surface != Surface.Expanded)
            {
                return;
            }

            HideFace(CompactFace);
            HideFace(IdleFace);
            HideFace(DropFace);
        });

        Activate();
        _suppressDeactivateUntil = DateTime.Now.AddMilliseconds(700);
    }

    public void Collapse()
    {
        if (_surface != Surface.Expanded)
        {
            return;
        }

        Fade(ExpandedFace, 0, 130);
        var activity = ResolveActivity();
        var id = ++_animationId;

        if (activity is not null)
        {
            _surface = Surface.Compact;
            SetInteraction(Surface.Compact);
            UpdateCompact(activity);

            CompactFace.Visibility = Visibility.Visible;
            SetOpacity(CompactFace, 0.2);
            Fade(CompactFace, 1, 150, 0);
            Fade(IdleFace, 0, 90);

            AnimateTo(CompactWidth, CompactHeight, CompactHeight / 2, 280, () =>
            {
                if (id != _animationId || _surface != Surface.Compact)
                {
                    return;
                }

                HideFace(ExpandedFace);
                HideFace(IdleFace);
            });
        }
        else
        {
            _surface = Surface.Idle;
            SetInteraction(Surface.Idle);

            IdleFace.Visibility = Visibility.Visible;
            SetOpacity(IdleFace, 0.2);
            Fade(IdleFace, 1, 150, 0);
            Fade(CompactFace, 0, 90);

            AnimateTo(IdleWidth, IdleHeight, IdleHeight / 2, 280, () =>
            {
                if (id != _animationId || _surface != Surface.Idle)
                {
                    return;
                }

                HideFace(ExpandedFace);
                HideFace(CompactFace);
            });
        }
    }

    private void ShowIdle(bool force = false)
    {
        if (!force && _surface == Surface.Idle)
        {
            return;
        }

        HideSliverFace();
        _surface = Surface.Idle;
        SetInteraction(Surface.Idle);
        var id = ++_animationId;

        IdleFace.Visibility = Visibility.Visible;
        SetOpacity(IdleFace, 0.2);
        Fade(IdleFace, 1, 150, 0);
        Fade(CompactFace, 0, 100);
        Fade(ExpandedFace, 0, 110);

        AnimateTo(IdleWidth, IdleHeight, IdleHeight / 2, 260, () =>
        {
            if (id != _animationId || _surface != Surface.Idle)
            {
                return;
            }

            HideFace(CompactFace);
            HideFace(ExpandedFace);
            HideFace(DropFace);
        });
    }

    private void ShowCompact(LiveActivity activity)
    {
        if (_surface is Surface.Expanded or Surface.Drop)
        {
            return;
        }

        HideSliverFace();
        var resizing = _surface != Surface.Compact;
        _surface = Surface.Compact;
        SetInteraction(Surface.Compact);
        UpdateCompact(activity);

        CompactFace.Visibility = Visibility.Visible;
        if (!resizing)
        {
            SetOpacity(CompactFace, 1);
            return;
        }

        SetOpacity(CompactFace, 0.2);
        Fade(CompactFace, 1, 160, 0);
        Fade(IdleFace, 0, 120);

        var id = ++_animationId;
        AnimateTo(CompactWidth, CompactHeight, CompactHeight / 2, 260, () =>
        {
            if (id == _animationId && _surface == Surface.Compact)
            {
                HideFace(IdleFace);
            }
        });
    }

    private void Present()
    {
        if (_isAnimating || _surface is Surface.Expanded or Surface.Drop or Surface.Sliver)
        {
            return;
        }

        if (_transient is not null && DateTime.Now >= _transientUntil)
        {
            _transient = null;
        }

        var activity = ResolveActivity();
        if (activity is null)
        {
            ShowIdle();
        }
        else
        {
            ShowCompact(activity);
        }
    }

    private void ShowSliver(bool animate = true)
    {
        if (!(_edgeMode || _smartMode) || _surface is Surface.Expanded or Surface.Drop or Surface.Sliver)
        {
            return;
        }

        _surface = Surface.Sliver;
        SetInteraction(Surface.Sliver);
        _transient = null;
        _edgeHideAt = DateTime.MinValue;
        _edgeHoverSince = DateTime.MinValue;
        _smartHideAt = DateTime.MinValue;
        _smartRevealAt = DateTime.MinValue;

        SliverFace.Visibility = Visibility.Visible;
        SetOpacity(SliverFace, 0);
        Fade(SliverFace, 1, 140, 40);
        Fade(IdleFace, 0, 80);
        Fade(CompactFace, 0, 80);
        Fade(DropFace, 0, 80);

        var id = ++_animationId;
        AnimateTo(SliverWidth, SliverHeight, SliverHeight / 2, animate ? 220 : 0, () =>
        {
            if (id != _animationId || _surface != Surface.Sliver)
            {
                return;
            }

            HideFace(IdleFace);
            HideFace(CompactFace);
            HideFace(DropFace);
            UnloadActivePane();
            ScheduleMemoryTrim();
        });
    }

    private void RevealFromSliver()
    {
        if (!(_edgeMode || _smartMode) || _surface != Surface.Sliver)
        {
            return;
        }

        HideSliverFace();
        _edgeHideAt = DateTime.MinValue;
        _edgeHoverSince = DateTime.MinValue;
        _smartHideAt = DateTime.MinValue;
        _smartRevealAt = DateTime.MinValue;

        var activity = ResolveActivity();
        if (activity is null)
        {
            ShowIdle();
        }
        else
        {
            ShowCompact(activity);
        }
    }

    private void HideSliverFace()
    {
        SliverFace.Visibility = Visibility.Collapsed;
        SliverFace.Opacity = 0;
    }

    private void EdgeTick()
    {
        if ((!_edgeMode && !_smartMode) || _isAnimating || _fullscreenHidden || !IsVisible)
        {
            return;
        }

        if (_surface == Surface.Sliver)
        {
            if (IsCursorInEdgeHotZone())
            {
                if (_edgeHoverSince == DateTime.MinValue)
                {
                    _edgeHoverSince = DateTime.Now;
                    return;
                }

                if ((DateTime.Now - _edgeHoverSince).TotalMilliseconds >= 250)
                {
                    _edgeHoverSince = DateTime.MinValue;
                    RevealFromSliver();
                }

                return;
            }

            _edgeHoverSince = DateTime.MinValue;

            if (!_smartMode)
            {
                return;
            }

            if (IsIslandAreaCovered())
            {
                _smartRevealAt = DateTime.MinValue;
                return;
            }

            if (_smartRevealAt == DateTime.MinValue)
            {
                _smartRevealAt = DateTime.Now.AddMilliseconds(700);
                return;
            }

            if (DateTime.Now >= _smartRevealAt)
            {
                _smartRevealAt = DateTime.MinValue;
                RevealFromSliver();
            }

            return;
        }

        if (_surface is Surface.Expanded or Surface.Drop)
        {
            _edgeHideAt = DateTime.MinValue;
            _smartHideAt = DateTime.MinValue;
            return;
        }

        if (_surface is not (Surface.Idle or Surface.Compact))
        {
            return;
        }

        if (_compactSeeking)
        {
            _edgeHideAt = DateTime.MinValue;
            _smartHideAt = DateTime.MinValue;
            return;
        }

        if (_transient is not null && DateTime.Now >= _transientUntil)
        {
            _transient = null;
        }

        var inside = IsCursorOverWindow();
        if (inside || _transient is not null)
        {
            _edgeHideAt = DateTime.MinValue;
            _smartHideAt = DateTime.MinValue;
            return;
        }

        if (_edgeMode)
        {
            if (_edgeHideAt == DateTime.MinValue)
            {
                _edgeHideAt = DateTime.Now.AddSeconds(1.1);
                return;
            }

            if (DateTime.Now >= _edgeHideAt)
            {
                _edgeHideAt = DateTime.MinValue;
                ShowSliver();
            }

            return;
        }

        // Smart mode: stay visible until an app window actually occupies the
        // area around the island, then shrink to the sliver.
        if (!IsIslandAreaCovered())
        {
            _smartHideAt = DateTime.MinValue;
            return;
        }

        if (_smartHideAt == DateTime.MinValue)
        {
            _smartHideAt = DateTime.Now.AddMilliseconds(350);
            return;
        }

        if (DateTime.Now >= _smartHideAt)
        {
            _smartHideAt = DateTime.MinValue;
            ShowSliver();
        }
    }

    private bool IsIslandAreaCovered()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var centerX = rect.Left + (rect.Right - rect.Left) / 2;
        var halfWidth = (int)Math.Round((CompactWidth / 2 + 12) * dpi.DpiScaleX);
        var detectionBottom = rect.Top + (int)Math.Round((CompactHeight + 16) * dpi.DpiScaleY);
        var middleY = rect.Top + (detectionBottom - rect.Top) / 2;

        // Use a fixed footprint instead of the current sliver size. Otherwise a
        // window under the full island stops covering the tiny sliver, making
        // the island expand and hide forever.
        var detection = new Native.RECT
        {
            Left = centerX - halfWidth,
            Top = rect.Top,
            Right = centerX + halfWidth,
            Bottom = detectionBottom
        };

        var foreground = Native.GetForegroundWindow();
        if (foreground != _hwnd &&
            Native.GetWindowRect(foreground, out var foregroundRect) &&
            Intersects(foregroundRect, detection) &&
            IsAppWindow(foreground))
        {
            return true;
        }

        Span<Native.POINT> points =
        [
            new Native.POINT { X = centerX, Y = middleY },
            new Native.POINT { X = centerX, Y = detectionBottom },
            new Native.POINT { X = detection.Left, Y = middleY },
            new Native.POINT { X = detection.Right, Y = middleY },
            new Native.POINT { X = detection.Left, Y = detectionBottom },
            new Native.POINT { X = detection.Right, Y = detectionBottom }
        ];

        foreach (var point in points)
        {
            if (point.X < 0 || point.Y < 0)
            {
                continue;
            }

            var window = Native.WindowFromPoint(point);
            if (IsAppWindow(window))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Intersects(Native.RECT a, Native.RECT b)
    {
        return a.Left < b.Right && a.Right > b.Left &&
               a.Top < b.Bottom && a.Bottom > b.Top;
    }

    private bool IsAppWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || window == _hwnd)
        {
            return false;
        }

        var root = Native.GetAncestor(window, Native.GA_ROOT);
        if (root == IntPtr.Zero)
        {
            root = window;
        }

        if (root == _hwnd || !Native.IsWindowVisible(root))
        {
            return false;
        }

        var className = new StringBuilder(128);
        Native.GetClassName(root, className, className.Capacity);
        var cls = className.ToString();

        return !NonAppWindowClasses.Contains(cls);
    }

    private bool IsCursorOverWindow()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var rect) ||
            !Native.GetCursorPos(out var point))
        {
            return false;
        }

        return point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    private bool IsCursorInEdgeHotZone()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var rect) ||
            !Native.GetCursorPos(out var point))
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var horizontal = 30 * dpi.DpiScaleX;
        var vertical = 8 * dpi.DpiScaleY;

        return point.X >= rect.Left - horizontal && point.X <= rect.Right + horizontal &&
               point.Y >= rect.Top - 2 * dpi.DpiScaleY &&
               point.Y <= rect.Bottom + vertical;
    }

    private LiveActivity? ResolveActivity()
    {
        if (_transient is not null)
        {
            if (DateTime.Now < _transientUntil)
            {
                return _transient;
            }

            _transient = null;
        }

        if (_s.Timers.CountdownActive)
        {
            return BuildTimerActivity();
        }

        var media = _s.Media.Current;
        if (media.HasSession && (media.IsPlaying || DateTime.Now < _mediaHoldUntil))
        {
            return BuildMediaActivity(media);
        }

        return null;
    }

    private void OnPresentTimer()
    {
        if (_isAnimating)
        {
            return;
        }

        if (_surface == Surface.Expanded)
        {
            if (_transient is not null && DateTime.Now >= _transientUntil)
            {
                _transient = null;
            }

            return;
        }

        Present();
    }

    private void OnTimersUpdated()
    {
        if (_isAnimating || _surface != Surface.Compact)
        {
            return;
        }

        var activity = ResolveActivity();
        if (activity is not null)
        {
            UpdateCompact(activity);
        }
    }

    // Compact rendering
    private void UpdateCompact(LiveActivity activity)
    {
        _currentActivity = activity;

        if (activity.Art is not null)
        {
            CompactArtBrush.ImageSource = activity.Art;
            CompactArtHost.Visibility = Visibility.Visible;
            CompactArtTile.Visibility = Visibility.Collapsed;
        }
        else
        {
            CompactArtBrush.ImageSource = null;
            CompactArtHost.Visibility = Visibility.Collapsed;
            CompactArtTile.Visibility = Visibility.Visible;
            CompactGlyph.Text = activity.Glyph;
            CompactGlyph.Foreground = activity.Accent ?? TextPrimary;
            CompactArtTile.Background = MakeTileBrush(activity.Accent);
        }

        CompactTitle.Text = activity.Title;
        CompactSubtitle.Text = _compactSeeking && activity.Kind == ActivityKind.Media
            ? FormatCountdown(TimeSpan.FromSeconds(_s.Media.Current.Duration.TotalSeconds * _compactSeekFraction))
            : activity.Subtitle;
        CompactSubtitle.Visibility = string.IsNullOrWhiteSpace(CompactSubtitle.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        CompactBar.Visibility = activity.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        CompactBar.Value = _compactSeeking && activity.Kind == ActivityKind.Media
            ? _compactSeekFraction * 100
            : Math.Clamp(activity.Progress, 0, 1) * 100;

        var seekable = activity.Kind == ActivityKind.Media && _s.Media.Current.Duration > TimeSpan.Zero;
        CompactSeekHit.Visibility = seekable ? Visibility.Visible : Visibility.Collapsed;

        CompactRing.Visibility = activity.ShowRing ? Visibility.Visible : Visibility.Collapsed;
        CompactRing.Value = Math.Clamp(activity.Progress, 0, 1);
        CompactRing.Foreground = activity.Accent ?? Accent;

        CompactEqualizer.Visibility = activity.Kind == ActivityKind.Media && activity.IsPlaying
            ? Visibility.Visible
            : Visibility.Collapsed;

        CompactPlayButton.Visibility = activity.ShowMediaControls ? Visibility.Visible : Visibility.Collapsed;
        CompactPlayButton.Content = activity.IsPlaying ? "\uE769" : "\uE768";
    }

    private LiveActivity BuildMediaActivity(MediaSnapshot media)
    {
        var progress = media.Duration > TimeSpan.Zero
            ? Math.Clamp(media.CurrentPosition.TotalSeconds / media.Duration.TotalSeconds, 0, 1)
            : 0;

        var subtitle = string.IsNullOrWhiteSpace(media.Artist) ? media.App : $"{media.Artist} · {media.App}";

        return new LiveActivity
        {
            Kind = ActivityKind.Media,
            Glyph = "\uE768",
            Title = media.Title,
            Subtitle = subtitle,
            Art = media.Art,
            Progress = progress,
            ShowProgress = media.Duration > TimeSpan.Zero,
            ShowMediaControls = true,
            IsPlaying = media.IsPlaying
        };
    }

    private LiveActivity BuildTimerActivity()
    {
        var timers = _s.Timers;
        var remaining = FormatCountdown(timers.CountdownRemaining);
        var subtitle = timers.CountdownPaused ? $"已暂停 · {remaining}" : $"剩余 {remaining}";

        return new LiveActivity
        {
            Kind = ActivityKind.Timer,
            Glyph = "\uE823",
            Title = timers.CountdownLabel,
            Subtitle = subtitle,
            Progress = timers.CountdownProgress,
            ShowRing = true
        };
    }

    private LiveActivity BuildVolumeActivity(float volume, bool muted)
    {
        var percent = (int)Math.Round(volume * 100);
        return new LiveActivity
        {
            Kind = ActivityKind.Volume,
            Glyph = muted ? "\uE74F" : "\uE767",
            Title = muted ? "已静音" : "音量",
            Subtitle = muted ? $"{percent}% · 已静音" : $"{percent}%",
            Progress = volume,
            ShowProgress = true,
            Accent = muted ? Red : null
        };
    }

    private LiveActivity BuildBrightnessActivity(int percent)
    {
        return new LiveActivity
        {
            Kind = ActivityKind.Brightness,
            Glyph = "\uE706",
            Title = "亮度",
            Subtitle = $"{percent}%",
            Progress = percent / 100d,
            ShowProgress = true
        };
    }

    private LiveActivity BuildBatteryActivity()
    {
        var power = _s.Power;
        var hasBattery = power.HasBattery;
        var title = hasBattery ? $"电量 {power.Percent}%" : "电源已接通";
        var subtitle = power.StateText;
        if (!string.IsNullOrWhiteSpace(power.TimeText) && !power.IsCharging)
        {
            subtitle = $"{subtitle} · {power.TimeText}";
        }

        return new LiveActivity
        {
            Kind = ActivityKind.Battery,
            Glyph = "\uE83F",
            Title = title,
            Subtitle = subtitle,
            Progress = power.Percent / 100d,
            ShowProgress = hasBattery,
            Accent = power.IsCharging ? Green : null
        };
    }

    private LiveActivity BuildClipboardActivity(ClipboardItem item)
    {
        return new LiveActivity
        {
            Kind = ActivityKind.Clipboard,
            Glyph = item.KindGlyph,
            Title = "已复制到剪贴板",
            Subtitle = item.Preview,
            Accent = AccentBrush
        };
    }

    private LiveActivity BuildAlertActivity(IslandAlert alert)
    {
        return new LiveActivity
        {
            Kind = ActivityKind.Alert,
            Glyph = alert.Glyph,
            Title = alert.Title,
            Subtitle = alert.Message,
            Art = alert.Icon,
            Accent = alert.Icon is null ? AlertBrush(alert.Kind) : null
        };
    }

    private LiveActivity BuildShelfActivity(int count)
    {
        return new LiveActivity
        {
            Kind = ActivityKind.Info,
            Glyph = "\uE8B7",
            Title = "已加入文件架",
            Subtitle = $"{count} 个项目"
        };
    }

    // Service events
    private void OnMediaUpdated()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnMediaUpdated));
            return;
        }

        var media = _s.Media.Current;
        if (media.HasSession && media.IsPlaying)
        {
            _lastMediaPlaying = true;
            _mediaHoldUntil = DateTime.MaxValue;
        }
        else if (media.HasSession && _lastMediaPlaying)
        {
            _lastMediaPlaying = false;
            _mediaHoldUntil = DateTime.Now.AddSeconds(7);
        }
        else if (!media.HasSession)
        {
            _lastMediaPlaying = false;
            _mediaHoldUntil = DateTime.MinValue;
        }

        if (_surface is not (Surface.Expanded or Surface.Drop))
        {
            Present();
        }
    }

    private void OnAudioChanged(float volume, bool muted)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnAudioChanged(volume, muted)));
            return;
        }

        if (_surface == Surface.Expanded)
        {
            return;
        }

        ShowTransient(BuildVolumeActivity(volume, muted), 2.2);
    }

    private void OnBrightnessChanged(int percent)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnBrightnessChanged(percent)));
            return;
        }

        if (_surface == Surface.Expanded)
        {
            return;
        }

        ShowTransient(BuildBrightnessActivity(percent), 2.2);
    }

    private void OnAcChanged(bool charging)
    {
        if (_surface != Surface.Expanded)
        {
            ShowTransient(BuildBatteryActivity(), 3.5);
        }
    }

    private void OnLowBattery(int percent)
    {
        PushAlert("电量偏低", $"剩余 {percent}%，建议连接电源。", "电源", "warning", 4);
    }

    private void OnClipboardItemAdded(ClipboardItem item)
    {
        if (_surface == Surface.Expanded)
        {
            return;
        }

        ShowTransient(BuildClipboardActivity(item), 3.2);
    }

    private void OnCountdownCompleted()
    {
        PushAlert("计时结束", $"「{_s.Timers.CountdownLabel}」已完成。", "计时器", "timer", 6);
    }

    private void OnForegroundAppChanged(AppSwitchInfo info)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnForegroundAppChanged(info)));
            return;
        }

        if (!info.IsNewlyOpened)
        {
            // Switching between already running apps is the noisiest event and
            // tends to cover browser tabs. It is opt-in, and while media is
            // playing the compact island should stay on Now Playing instead.
            if (!_s.Settings.Current.ShowAppSwitches || _s.Media.Current.IsPlaying)
            {
                return;
            }

            if ((DateTime.Now - _lastAppSwitchShown).TotalSeconds < 4)
            {
                return;
            }
        }

        _lastAppSwitchShown = DateTime.Now;

        var title = (info.IsNewlyOpened ? "已打开 " : "已切换到 ") + info.DisplayName;
        var subtitle = info.WindowTitle;

        if (string.IsNullOrWhiteSpace(subtitle) ||
            string.Equals(subtitle, info.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            subtitle = info.ProcessName;
        }

        ShowTransient(new LiveActivity
        {
            Kind = ActivityKind.App,
            Glyph = "\uE71D",
            Title = title,
            Subtitle = subtitle,
            Art = info.Icon,
            Accent = info.Icon is null ? AccentBrush : null
        }, 2.8);
    }

    private void OnSystemNotification(SystemNotificationInfo info)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSystemNotification(info)));
            return;
        }

        if (!_s.Settings.Current.ShowSystemNotifications)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(info.Title) ? info.AppName : info.Title;
        PushAlert(title, info.Body, info.AppName, "system", 6, info.Icon);
    }

    // Seeking
    public async Task<bool> SeekMediaAsync(TimeSpan target)
    {
        var media = _s.Media.Current;
        if (!media.HasSession || media.Duration <= TimeSpan.Zero)
        {
            return false;
        }

        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }
        else if (target > media.Duration)
        {
            target = media.Duration;
        }

        if (Math.Abs((target - media.CurrentPosition).TotalSeconds) < 1)
        {
            return true;
        }

        await _s.Media.SeekAsync(target);
        await Task.Delay(400);
        var after = _s.Media.Current.CurrentPosition;

        // Some players return success from SMTC but ignore the position change
        // (QQ Music is one of them). Report that clearly instead of silently snapping back.
        return Math.Abs((target - after).TotalSeconds) <= 4;
    }

    private void CompactSeek_Down(object sender, MouseButtonEventArgs e)
    {
        if (_currentActivity?.Kind != ActivityKind.Media || _s.Media.Current.Duration <= TimeSpan.Zero)
        {
            return;
        }

        _compactSeeking = true;
        Vm.IsSeeking = true;
        CompactSeekHit.CaptureMouse();
        UpdateCompactSeek(e.GetPosition(CompactSeekHit).X);
        e.Handled = true;
    }

    private void CompactSeek_Move(object sender, MouseEventArgs e)
    {
        if (!_compactSeeking)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndCompactSeek(false);
            return;
        }

        UpdateCompactSeek(e.GetPosition(CompactSeekHit).X);
        e.Handled = true;
    }

    private async void CompactSeek_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_compactSeeking)
        {
            return;
        }

        e.Handled = true;
        UpdateCompactSeek(e.GetPosition(CompactSeekHit).X);
        var duration = _s.Media.Current.Duration;
        var target = TimeSpan.FromSeconds(duration.TotalSeconds * _compactSeekFraction);
        _compactSeeking = false;
        CompactSeekHit.ReleaseMouseCapture();

        var supported = await SeekMediaAsync(target);
        Vm.IsSeeking = false;
        Present();

        if (!supported)
        {
            ShowTransient(new LiveActivity
            {
                Kind = ActivityKind.Alert,
                Glyph = "\uE7BA",
                Title = "无法跳转进度",
                Subtitle = "当前播放器不支持快进 / 快退",
                Accent = Orange
            }, 3);
        }
    }

    private void CompactSeek_Lost(object sender, MouseEventArgs e)
    {
        if (_compactSeeking)
        {
            EndCompactSeek(true);
        }
    }

    private void EndCompactSeek(bool restore)
    {
        _compactSeeking = false;
        Vm.IsSeeking = false;
        if (restore)
        {
            Present();
        }
    }

    private void UpdateCompactSeek(double x)
    {
        var width = CompactSeekHit.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        _compactSeekFraction = Math.Clamp(x / width, 0, 1);
        CompactBar.Value = _compactSeekFraction * 100;
        var duration = _s.Media.Current.Duration;
        CompactSubtitle.Text = FormatCountdown(TimeSpan.FromSeconds(duration.TotalSeconds * _compactSeekFraction));
        CompactSubtitle.Visibility = Visibility.Visible;
    }

    private void ShowTransient(LiveActivity activity, double seconds)
    {
        _transient = activity;
        _transientUntil = DateTime.Now.AddSeconds(seconds);

        if ((_edgeMode || _smartMode) && _surface == Surface.Sliver)
        {
            _edgeHideAt = DateTime.Now.AddSeconds(seconds + 1.2);
            RevealFromSliver();
            return;
        }

        if (_isAnimating || _surface is Surface.Expanded or Surface.Drop)
        {
            return;
        }

        Present();
    }

    // Alerts
    public IslandAlert PushAlert(string title, string message, string app = "WinIsland", string kind = "info",
        double bannerSeconds = 5, ImageSource? icon = null)
    {
        var alert = new IslandAlert
        {
            Title = string.IsNullOrWhiteSpace(title) ? "通知" : title,
            Message = message,
            App = string.IsNullOrWhiteSpace(app) ? "WinIsland" : app,
            Kind = string.IsNullOrWhiteSpace(kind) ? "info" : kind,
            Icon = icon
        };

        Vm.AddAlert(alert);

        if (_s.Settings.Current.PlaySound)
        {
            try
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
            catch
            {
                // Audio device unavailable.
            }
        }

        ShowTransient(BuildAlertActivity(alert), bannerSeconds);
        return alert;
    }

    // Pane selection
    private void SelectPane(int index)
    {
        if (Nav.SelectedIndex == index)
        {
            ShowPane(index);
        }
        else
        {
            Nav.SelectedIndex = index;
        }
    }

    private void ShowPane(int index)
    {
        if (index < 0 || index > 7)
        {
            return;
        }

        // While the island is a sliver the panel isn't visible; keep the visual
        // tree empty so idle memory stays low.
        if (_surface == Surface.Sliver)
        {
            return;
        }

        if (_activePaneIndex == index && _activePane is not null)
        {
            if (index == 6)
            {
                Vm.MarkAllRead();
            }

            return;
        }

        UnloadActivePane();
        _activePane = CreatePane(index);
        _activePaneIndex = index;

        if (_activePane is not null)
        {
            _activePane.DataContext = DataContext;
            PaneHost.Children.Add(_activePane);
        }

        if (index == 6)
        {
            Vm.MarkAllRead();
        }
    }

    private static UserControl? CreatePane(int index)
    {
        return index switch
        {
            0 => new NowPlayingView(),
            1 => new TimerView(),
            2 => new CalendarView(),
            3 => new SystemView(),
            4 => new ClipboardView(),
            5 => new ShelfView(),
            6 => new AlertsView(),
            7 => new SettingsView(),
            _ => null
        };
    }

    private void UnloadActivePane()
    {
        if (_activePane is null)
        {
            return;
        }

        PaneHost.Children.Remove(_activePane);
        _activePane.DataContext = null;
        _activePane = null;
        _activePaneIndex = -1;
    }

    private void ScheduleMemoryTrim()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (!(_edgeMode || _smartMode) || _surface != Surface.Sliver)
            {
                return;
            }

            if ((DateTime.Now - _lastMemoryTrim).TotalSeconds < 30)
            {
                return;
            }

            _lastMemoryTrim = DateTime.Now;

            // The panel was just removed from the visual tree. Collect while
            // hidden so switching panes over time doesn't leave WPF trees behind.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, true, true);
        }));
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex >= 0)
        {
            ShowPane(Nav.SelectedIndex);
        }
    }

    // Mouse / keyboard
    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_surface is Surface.Expanded or Surface.Drop)
        {
            return;
        }

        Expand();
        e.Handled = true;
    }

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((_edgeMode || _smartMode) && _surface == Surface.Sliver)
        {
            RevealFromSliver();
        }
    }

    private void Island_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        Expand(7);
        e.Handled = true;
    }

    private async void CompactPlay_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DebugLog("CompactPlay_Click kind=" + _currentActivity?.Kind);
        if (_currentActivity?.Kind == ActivityKind.Media)
        {
            await _s.Media.TogglePlayPauseAsync();
        }
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SelectPane(7);
    }

    private void OnGlobalClick(Native.POINT point)
    {
        if (_surface != Surface.Expanded || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!Native.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        if (point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom)
        {
            Collapse();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_mouseHook.IsActive || DateTime.Now < _suppressDeactivateUntil)
        {
            return;
        }

        if (_surface == Surface.Expanded)
        {
            Collapse();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Collapse();
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    // File drop
    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Effects = DragDropEffects.Copy;
        EnterDropMode();
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        ExitDropMode();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            ExitDropMode();
            return;
        }

        var added = _s.Shelf.Add(files);
        if (added > 0)
        {
            if (_surface == Surface.Expanded)
            {
                SelectPane(5);
            }
            else
            {
                ShowTransient(BuildShelfActivity(added), 3.5);
            }
        }

        ExitDropMode();
        e.Handled = true;
    }

    private void EnterDropMode()
    {
        if (_surface is Surface.Expanded or Surface.Drop)
        {
            return;
        }

        HideSliverFace();
        _surface = Surface.Drop;
        SetInteraction(Surface.Drop);

        DropFace.Visibility = Visibility.Visible;
        SetOpacity(DropFace, 0);
        Fade(DropFace, 1, 150);
        Fade(IdleFace, 0, 100);
        Fade(CompactFace, 0, 100);

        var id = ++_animationId;
        AnimateTo(DropWidth, DropHeight, 24, 210, () =>
        {
            if (id == _animationId && _surface == Surface.Drop)
            {
                HideFace(IdleFace);
                HideFace(CompactFace);
            }
        });
    }

    private void ExitDropMode()
    {
        if (_surface != Surface.Drop)
        {
            return;
        }

        HideFace(DropFace);
        _surface = Surface.Idle;
        SetInteraction(Surface.Idle);

        var activity = ResolveActivity();
        if (activity is not null)
        {
            ShowCompact(activity);
        }
        else
        {
            ShowIdle(force: true);
        }
    }

    // Fullscreen detection
    private void CheckFullscreen()
    {
        if (!_s.Settings.Current.HideInFullscreen)
        {
            if (_fullscreenHidden)
            {
                RestoreFromFullscreen();
            }

            return;
        }

        if (_surface is Surface.Expanded or Surface.Drop)
        {
            return;
        }

        if (_fullscreenHidden)
        {
            if (!IsForegroundFullscreen())
            {
                RestoreFromFullscreen();
            }

            return;
        }

        if (IsForegroundFullscreen())
        {
            _fullscreenHidden = true;
            _transient = null;
            Hide();
        }
    }

    private void RestoreFromFullscreen()
    {
        _fullscreenHidden = false;
        Show();
        RepositionWindow();
        Present();
    }

    private bool IsForegroundFullscreen()
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd)
        {
            return false;
        }

        var className = new StringBuilder(128);
        Native.GetClassName(foreground, className, className.Capacity);
        var name = className.ToString();
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow" or "ForegroundStaging")
        {
            return false;
        }

        if (!Native.GetWindowRect(foreground, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        var monitor = Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        const int tolerance = 2;
        return rect.Left <= info.rcMonitor.Left + tolerance &&
               rect.Top <= info.rcMonitor.Top + tolerance &&
               rect.Right >= info.rcMonitor.Right - tolerance &&
               rect.Bottom >= info.rcMonitor.Bottom - tolerance;
    }

    // Helpers
    private static string FormatCountdown(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private Brush MakeTileBrush(Brush? accent)
    {
        if (accent is SolidColorBrush solid)
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x30, solid.Color.R, solid.Color.G, solid.Color.B));
            brush.Freeze();
            return brush;
        }

        return (Brush)FindResource("IconTileBrush");
    }

    private Brush TextPrimary => (Brush)FindResource("TextPrimaryBrush");
    private Brush Accent => (Brush)FindResource("AccentBrush");
    private Brush AccentBrush => (Brush)FindResource("AccentBrush");
    private Brush Green => (Brush)FindResource("GreenBrush");
    private Brush Red => (Brush)FindResource("RedBrush");
    private Brush Orange => (Brush)FindResource("OrangeBrush");

    private Brush AlertBrush(string kind) => kind switch
    {
        "success" => Green,
        "warning" => Orange,
        "error" => Red,
        "timer" => (Brush)FindResource("PurpleBrush"),
        _ => Accent
    };

    internal static void DebugLog(string message)
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
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Debug logging only.
        }
    }
}
