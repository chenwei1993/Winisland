using System.Windows;
using System.Windows.Controls;

namespace WinIsland.Panes;

public partial class SystemView : UserControl
{
    private bool _ready;

    public SystemView()
    {
        InitializeComponent();

        // Sliders are created with Value=0 before the bindings are applied. Only
        // forward changes that come from real user input, otherwise opening this
        // pane would apply that transient 0 and mute the system / dim the monitor.
        Loaded += (_, _) => _ready = true;
        Unloaded += (_, _) => _ready = false;
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || sender is not Slider slider || !IsUserEditing(slider))
        {
            return;
        }

        var audio = App.Services.Audio;
        var target = e.NewValue / 100d;
        if (Math.Abs(target - audio.Volume) > 0.01)
        {
            audio.SetVolume(target);
        }
    }

    private void Brightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || sender is not Slider slider || !IsUserEditing(slider))
        {
            return;
        }

        var brightness = App.Services.Brightness;
        if (!brightness.IsAvailable)
        {
            return;
        }

        var target = (int)Math.Round(e.NewValue);
        if (Math.Abs(target - brightness.Percent) >= 1)
        {
            brightness.SetPercent(target);
        }
    }

    private static bool IsUserEditing(Slider slider)
    {
        return slider.IsKeyboardFocusWithin || slider.IsMouseCaptureWithin || slider.IsStylusCaptureWithin;
    }
}
