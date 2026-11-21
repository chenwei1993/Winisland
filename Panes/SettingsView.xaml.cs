using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinIsland.Services;

namespace WinIsland.Panes;

public partial class SettingsView : UserControl
{
    private UpdateService? _updates;
    private string? _actionError;
    private bool _autoScrolled;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => AttachUpdates();
        Unloaded += (_, _) => DetachUpdates();
    }

    private void AttachUpdates()
    {
        if (_updates is not null)
        {
            return;
        }

        _updates = App.Services.Update;
        _updates.StateChanged += RefreshUpdate;
        _autoScrolled = false;
        RefreshUpdate();
    }

    private void DetachUpdates()
    {
        if (_updates is null)
        {
            return;
        }

        _updates.StateChanged -= RefreshUpdate;
        _updates = null;
    }

    private void RefreshUpdate()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshUpdate));
            return;
        }

        var updates = _updates;
        if (updates is null)
        {
            return;
        }

        var latest = updates.Latest;
        var tag = latest?.Tag ?? "新版本";
        var current = updates.CurrentVersionText;

        UpdateBar.Visibility = Visibility.Collapsed;
        UpdateActionButton.IsEnabled = true;
        UpdatePageLink.Visibility = Visibility.Visible;

        switch (updates.State)
        {
            case UpdateState.Checking:
                UpdateTitle.Text = "版本更新";
                UpdateStatus.Text = "正在检查新版本…";
                UpdateActionButton.Content = "检查中";
                UpdateActionButton.IsEnabled = false;
                break;

            case UpdateState.UpToDate:
                UpdateTitle.Text = "已是最新版本";
                UpdateStatus.Text = updates.LastChecked is { } checkedAt
                    ? $"当前 {current} · 上次检查 {checkedAt:HH:mm}"
                    : $"当前 {current}";
                UpdateActionButton.Content = "检查更新";
                break;

            case UpdateState.Available:
                UpdateTitle.Text = $"发现新版本 {tag}";
                UpdateStatus.Text = _actionError ?? $"当前 {current} · 安装包约 {latest?.SizeText}";
                UpdateActionButton.Content = "下载更新";
                break;

            case UpdateState.Downloading:
                UpdateTitle.Text = $"正在下载 {tag}";
                var percent = (int)Math.Round(updates.Progress * 100);
                var source = string.IsNullOrEmpty(updates.SourceText) ? string.Empty : $" · {updates.SourceText}";
                UpdateStatus.Text = $"{percent}% · 共 {latest?.SizeText}{source}";
                UpdateActionButton.Content = $"{percent}%";
                UpdateActionButton.IsEnabled = false;
                UpdateBar.Visibility = Visibility.Visible;
                UpdateBar.Value = percent;
                break;

            case UpdateState.Downloaded:
                UpdateTitle.Text = $"新版本 {tag} 已下载";
                UpdateStatus.Text = _actionError ?? "点击「重启并更新」完成安装";
                UpdateActionButton.Content = "重启并更新";
                break;

            case UpdateState.Installing:
                UpdateTitle.Text = "正在更新";
                UpdateStatus.Text = "程序即将重启…";
                UpdateActionButton.Content = "更新中";
                UpdateActionButton.IsEnabled = false;
                break;

            case UpdateState.Failed:
                UpdateTitle.Text = "更新失败";
                UpdateStatus.Text = _actionError ?? updates.Message ?? "请稍后重试";
                UpdateActionButton.Content = "重试";
                break;

            default:
                UpdateTitle.Text = "版本更新";
                UpdateStatus.Text = _actionError ?? $"当前 {current}";
                UpdateActionButton.Content = "检查更新";
                break;
        }

        if (!_autoScrolled &&
            updates.State is UpdateState.Available or UpdateState.Downloading or UpdateState.Downloaded)
        {
            _autoScrolled = true;
            Dispatcher.BeginInvoke(
                new Action(() => UpdateCard.BringIntoView(
                    new Rect(0, 0, UpdateCard.ActualWidth, UpdateCard.ActualHeight + 80))),
                DispatcherPriority.Background);
        }
    }

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        var updates = _updates;
        if (updates is null)
        {
            return;
        }

        _actionError = null;

        switch (updates.State)
        {
            case UpdateState.Available:
                await updates.DownloadAsync();
                break;

            case UpdateState.Downloaded:
                if (updates.RestartAndInstall(out var error))
                {
                    App.ExitApp();
                    return;
                }

                _actionError = error;
                break;

            case UpdateState.Failed:
                if (updates.Latest is not null)
                {
                    await updates.DownloadAsync();
                }
                else
                {
                    await updates.CheckAsync();
                }

                break;

            case UpdateState.Checking:
            case UpdateState.Downloading:
            case UpdateState.Installing:
                break;

            default:
                await updates.CheckAsync();
                break;
        }

        RefreshUpdate();
    }

    private void UpdatePage_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenUpdatePage();
    }

    private static void OpenUpdatePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(App.Services.Update.PageUrl)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore.
        }
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        (Window.GetWindow(this) as MainWindow)?.ShowTestNotification();
    }

    private void ManageUsage_Click(object sender, RoutedEventArgs e)
    {
        UsageSettingsWindow.ShowWindow();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.Services.Settings.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", App.Services.Settings.DataDirectory)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore.
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        App.ExitApp();
    }
}
