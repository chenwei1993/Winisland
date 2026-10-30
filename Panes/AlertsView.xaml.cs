using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinIsland.Models;

namespace WinIsland.Panes;

public partial class AlertsView : UserControl
{
    public AlertsView()
    {
        InitializeComponent();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        App.Island?.ClearAlerts();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: IslandAlert alert })
        {
            App.Island?.RemoveAlert(alert);
        }
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.MarkAllRead();
        }
    }
}
