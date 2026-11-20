using System.Windows;
using System.Windows.Controls;

namespace WinIsland.Panes;

public partial class UsageView : UserControl
{
    public UsageView()
    {
        InitializeComponent();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Services.Usage.RefreshNowAsync();
        }
        catch
        {
            // The card shows the failure state.
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        UsageSettingsWindow.ShowWindow();
    }
}
