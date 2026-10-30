using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinIsland.Models;

namespace WinIsland.Panes;

public partial class ShelfView : UserControl
{
    private Point _dragStart;
    private ShelfItem? _dragItem;

    public ShelfView()
    {
        InitializeComponent();
    }

    private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShelfItem item })
        {
            _dragStart = e.GetPosition(this);
            _dragItem = item;

            if (e.ClickCount == 2)
            {
                _dragItem = null;
                Open(item);
            }
        }
    }

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < 6 && Math.Abs(position.Y - _dragStart.Y) < 6)
        {
            return;
        }

        var item = _dragItem;
        _dragItem = null;

        if (File.Exists(item.Path) || Directory.Exists(item.Path))
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ShelfItem item })
        {
            Open(item);
        }
    }

    private static void Open(ShelfItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch
        {
            // Ignore missing / inaccessible files.
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ShelfItem item })
        {
            App.Services.Shelf.Remove(item);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Shelf.Clear();
    }
}
