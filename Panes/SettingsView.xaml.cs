using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace WinIsland.Panes;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        (Window.GetWindow(this) as MainWindow)?.ShowTestNotification();
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
