using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinIsland.Models;
using WinIsland.Services;
using WinIsland.Services.Usage;

namespace WinIsland;

/// <summary>
/// Dedicated configuration window for AI usage providers. One row per provider
/// is generated from <see cref="UsageService.ProviderInfos"/>, so adding a
/// provider later needs no new settings UI.
/// </summary>
public partial class UsageSettingsWindow : Window
{
    private static UsageSettingsWindow? _open;

    private readonly ServiceHub _s = App.Services;

    public UsageSettingsWindow()
    {
        ViewModel = App.Island!.Vm;
        InitializeComponent();
        DataContext = this;
        Topmost = _s.Settings.Current.AlwaysOnTop;

        // Keep the window inside the work area at high DPI / small screens.
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(580, workArea.Width - 40);
        Height = Math.Min(660, workArea.Height - 40);

        BuildRows();
    }

    public MainViewModel ViewModel { get; }

    public ObservableCollection<UsageProviderSettingRow> Rows { get; } = new();

    public static void ShowWindow()
    {
        if (_open is { IsVisible: true })
        {
            if (_open.WindowState == WindowState.Minimized)
            {
                _open.WindowState = WindowState.Normal;
            }

            _open.Activate();
            return;
        }

        var window = new UsageSettingsWindow();
        window.Closed += (_, _) => _open = null;
        _open = window;

        // The expanded island occupies the top half of the screen; collapsing it
        // keeps the configuration window unobstructed.
        App.Island?.Collapse();
        window.Show();
    }

    private void BuildRows()
    {
        foreach (var info in _s.Usage.ProviderInfos)
        {
            var row = new UsageProviderSettingRow(
                UsageProviderSettings.IsEnabled(_s.Settings.Current, info.Id))
            {
                ProviderId = info.Id,
                Name = info.Name,
                Glyph = info.Glyph,
                EnvironmentKey = info.EnvironmentKey,
                Description = info.Description,
                HasBalanceSettings = info.HasBalanceSettings,
                KeyStatus = _s.Usage.GetKeyStatus(info.Id),
                EnabledChanged = enabled =>
                {
                    UsageProviderSettings.SetEnabled(_s.Settings.Current, info.Id, enabled);
                    _s.Settings.Save();
                    _s.Usage.OnSettingsChanged();
                },
                BudgetChanged = value =>
                {
                    _s.Settings.Current.DeepSeekBudget = value;
                    _s.Settings.Save();
                    _s.Usage.OnSettingsChanged();
                },
                LowBalanceChanged = value =>
                {
                    _s.Settings.Current.DeepSeekLowBalance = value;
                    _s.Settings.Save();
                    _s.Usage.OnSettingsChanged();
                }
            };

            if (info.HasBalanceSettings && info.Id == "deepseek")
            {
                row.Budget = _s.Settings.Current.DeepSeekBudget;
                row.LowBalance = _s.Settings.Current.DeepSeekLowBalance;
            }

            Rows.Add(row);
        }
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UsageProviderSettingRow row })
        {
            return;
        }

        var key = row.KeyInput.Trim();
        if (key.Length == 0)
        {
            return;
        }

        _s.Usage.SetKey(row.ProviderId, key);
        row.KeyInput = string.Empty;
        row.KeyStatus = _s.Usage.GetKeyStatus(row.ProviderId);
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UsageProviderSettingRow row })
        {
            return;
        }

        _s.Usage.SetKey(row.ProviderId, null);
        row.KeyInput = string.Empty;
        row.KeyStatus = _s.Usage.GetKeyStatus(row.ProviderId);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
}
