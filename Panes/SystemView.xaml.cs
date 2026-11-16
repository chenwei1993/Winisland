using System.Windows;
using System.Windows.Controls;
using EncoDeck.Core.State;
using WinIsland.Models;

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
        Loaded += (_, _) =>
        {
            _ready = true;
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.RefreshOutputDevices();
            }
        };
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

    private void OutputDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (OutputDeviceMenu.Visibility == Visibility.Visible)
        {
            OutputDeviceMenu.Visibility = Visibility.Collapsed;
            OutputDeviceChevron.Text = "\uE70D";
            return;
        }

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.RefreshOutputDevices();
        }

        OutputDeviceMenu.Visibility = Visibility.Visible;
        OutputDeviceChevron.Text = "\uE70E";
    }

    private void OutputDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AudioOutputDevice device })
        {
            return;
        }

        OutputDeviceMenu.Visibility = Visibility.Collapsed;
        OutputDeviceChevron.Text = "\uE70D";
        App.Services.Audio.SetDefaultDevice(device.Id);
    }

    private async void BudsReconnect_Click(object sender, RoutedEventArgs e)
    {
        await App.Services.Buds.ReconnectAsync();
    }

    private void BudsDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        BudsDeviceMenu.Visibility = BudsDeviceMenu.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void BudsDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BudsDeviceOption device })
        {
            return;
        }

        BudsDeviceMenu.Visibility = Visibility.Collapsed;
        await App.Services.Buds.SelectDeviceAsync(device);
    }

    private void BudsNoise_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse<NoiseControlMode>(tag, out var mode))
        {
            return;
        }

        App.Services.Buds.SetNoiseControl(mode);
    }

    private void BudsEq_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string presetId })
        {
            App.Services.Buds.SetEqualizer(presetId);
        }
    }

    private void BudsSpatial_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse<SpatialAudioMode>(tag, out var mode))
        {
            App.Services.Buds.SetSpatialAudio(mode);
        }
    }

    private void BudsOptionChoice_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: BudsOptionChoice choice } button)
        {
            // GroupName is not a dependency property, so it cannot be bound.
            button.GroupName = "BudsOption_" + choice.RowId;
        }
    }

    private void BudsOptionChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BudsOptionChoice choice })
        {
            App.Services.Buds.SetOption(choice.RowId, choice.Value);
        }
    }

    private void BudsFind_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Buds.ToggleFindEarbuds();
    }

    private void BudsFitTest_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Buds.RunFitTest();
    }

    private void BudsOption_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || sender is not Slider { Tag: string id } slider || !IsUserEditing(slider))
        {
            return;
        }

        App.Services.Buds.SetOption(id, (int)Math.Round(e.NewValue));
    }

    private static bool IsUserEditing(Slider slider)
    {
        return slider.IsKeyboardFocusWithin || slider.IsMouseCaptureWithin || slider.IsStylusCaptureWithin;
    }
}
