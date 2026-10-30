using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinIsland.Models;

namespace WinIsland.Panes;

public partial class ClipboardView : UserControl
{
    public ClipboardView()
    {
        InitializeComponent();
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipboardItem item })
        {
            App.Services.Clipboard.Copy(item);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ClipboardItem item })
        {
            App.Services.Clipboard.Remove(item);
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RecordClipboard = !vm.RecordClipboard;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Clipboard.Clear();
    }
}
