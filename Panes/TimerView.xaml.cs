using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinIsland.Models;

namespace WinIsland.Panes;

public partial class TimerView : UserControl
{
    public TimerView()
    {
        InitializeComponent();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (CountdownPanel is null || StopwatchPanel is null || ReminderPanel is null)
        {
            return;
        }

        var countdown = TabCountdown.IsChecked == true;
        var stopwatch = TabStopwatch.IsChecked == true;

        CountdownPanel.Visibility = countdown ? Visibility.Visible : Visibility.Collapsed;
        StopwatchPanel.Visibility = stopwatch ? Visibility.Visible : Visibility.Collapsed;
        ReminderPanel.Visibility = !countdown && !stopwatch ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CountdownToggle_Click(object sender, RoutedEventArgs e)
    {
        var timers = App.Services.Timers;
        if (!timers.CountdownActive)
        {
            timers.StartCountdown(TimeSpan.FromMinutes(5), "计时");
        }
        else if (timers.CountdownPaused)
        {
            timers.ResumeCountdown();
        }
        else
        {
            timers.PauseCountdown();
        }
    }

    private void CountdownReset_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Timers.StopCountdown();
    }

    private void CountdownAdd_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Timers.AddMinute();
    }

    private void Preset1_Click(object sender, RoutedEventArgs e) => StartPreset(1, "计时");

    private void Preset3_Click(object sender, RoutedEventArgs e) => StartPreset(3, "计时");

    private void Preset5_Click(object sender, RoutedEventArgs e) => StartPreset(5, "计时");

    private void Preset10_Click(object sender, RoutedEventArgs e) => StartPreset(10, "计时");

    private void Preset25_Click(object sender, RoutedEventArgs e) => StartPreset(25, "专注");

    private static void StartPreset(int minutes, string label)
    {
        App.Services.Timers.StartCountdown(TimeSpan.FromMinutes(minutes), label);
    }

    private void StopwatchToggle_Click(object sender, RoutedEventArgs e)
    {
        var timers = App.Services.Timers;
        if (timers.StopwatchRunning)
        {
            timers.PauseStopwatch();
        }
        else
        {
            timers.StartStopwatch();
        }
    }

    private void StopwatchReset_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Timers.ResetStopwatch();
    }

    private void ReminderAdd_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseTarget(ReminderTimeBox.Text, out var target))
        {
            SetReminderHint("时间格式不对，请输入 HH:mm，例如 21:30", error: true);
            return;
        }

        var item = App.Services.Reminders.Add(target, ReminderTitleBox.Text);
        ReminderTitleBox.Text = string.Empty;
        SetReminderHint($"已添加：{item.TargetText}", error: false);
    }

    private void ReminderQuick5_Click(object sender, RoutedEventArgs e) => AddAfter(5);

    private void ReminderQuick15_Click(object sender, RoutedEventArgs e) => AddAfter(15);

    private void ReminderQuick30_Click(object sender, RoutedEventArgs e) => AddAfter(30);

    private void ReminderQuick60_Click(object sender, RoutedEventArgs e) => AddAfter(60);

    private void AddAfter(int minutes)
    {
        var item = App.Services.Reminders.AddAfter(TimeSpan.FromMinutes(minutes), ReminderTitleBox.Text);
        ReminderTitleBox.Text = string.Empty;
        SetReminderHint($"已添加：{item.TargetText}", error: false);
    }

    private void ReminderAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReminderItem item })
        {
            return;
        }

        App.Services.Reminders.Dismiss(item.Id);
        SetReminderHint(item.IsFired ? "已关闭提醒" : "已取消提醒", error: false);
    }

    private static bool TryParseTarget(string text, out DateTime target)
    {
        target = default;
        if (!DateTime.TryParseExact(text.Trim(), new[] { "H:mm", "HH:mm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return false;
        }

        var now = DateTime.Now;
        target = now.Date + time.TimeOfDay;
        if (target <= now)
        {
            target = target.AddDays(1);
        }

        return true;
    }

    private void SetReminderHint(string text, bool error)
    {
        ReminderHint.Text = text;
        ReminderHint.Foreground = (Brush)FindResource(error ? "RedBrush" : "TextTertiaryBrush");
    }
}
