using System.Windows;
using System.Windows.Controls;

namespace WinIsland.Panes;

public partial class TimerView : UserControl
{
    public TimerView()
    {
        InitializeComponent();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (CountdownPanel is null || StopwatchPanel is null)
        {
            return;
        }

        var countdown = TabCountdown.IsChecked == true;
        CountdownPanel.Visibility = countdown ? Visibility.Visible : Visibility.Collapsed;
        StopwatchPanel.Visibility = countdown ? Visibility.Collapsed : Visibility.Visible;
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
}
