using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinIsland.Panes;

public partial class NowPlayingView : UserControl
{
    private bool _seeking;
    private bool _ready;
    private DispatcherTimer? _hintTimer;

    public NowPlayingView()
    {
        InitializeComponent();

        // The volume slider starts at 0 before its OneWay binding is applied.
        // Ignore that transient value so opening this pane doesn't mute audio.
        Loaded += (_, _) => _ready = true;
        Unloaded += (_, _) => _ready = false;

    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        await App.Services.Media.TogglePlayPauseAsync();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        await App.Services.Media.PreviousAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        await App.Services.Media.NextAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await App.Services.Media.RefreshAsync();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Audio.ToggleMute();
    }

    private void Seek_Down(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { CanSeek: true } viewModel)
        {
            return;
        }

        _seeking = true;
        viewModel.IsSeeking = true;
        SeekHit.CaptureMouse();
        UpdateSeek(e.GetPosition(SeekHit).X);
        e.Handled = true;
    }

    private void Seek_Move(object sender, MouseEventArgs e)
    {
        if (!_seeking)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndSeek();
            return;
        }

        UpdateSeek(e.GetPosition(SeekHit).X);
        e.Handled = true;
    }

    private async void Seek_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking)
        {
            return;
        }

        e.Handled = true;
        UpdateSeek(e.GetPosition(SeekHit).X);
        _seeking = false;
        SeekHit.ReleaseMouseCapture();

        var viewModel = ViewModel;
        var window = Window.GetWindow(this) as MainWindow;
        var supported = true;

        if (viewModel is not null && window is not null)
        {
            supported = await window.SeekMediaAsync(TimeSpan.FromSeconds(SeekBar.Value));
        }

        if (viewModel is not null)
        {
            viewModel.IsSeeking = false;
        }

        if (!supported)
        {
            ShowSeekHint();
        }
    }

    private void Seek_Lost(object sender, MouseEventArgs e)
    {
        if (_seeking)
        {
            EndSeek();
        }
    }

    private void EndSeek()
    {
        _seeking = false;
        if (ViewModel is not null)
        {
            ViewModel.IsSeeking = false;
        }
    }

    private void UpdateSeek(double x)
    {
        var width = SeekHit.ActualWidth;
        if (width <= 0 || ViewModel is null)
        {
            return;
        }

        var fraction = Math.Clamp(x / width, 0, 1);
        SeekBar.Value = fraction * ViewModel.MediaDuration;
    }

    private void ShowSeekHint()
    {
        SeekHint.Visibility = Visibility.Visible;
        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer?.Stop();
            SeekHint.Visibility = Visibility.Collapsed;
        };
        _hintTimer.Start();
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || sender is not Slider slider ||
            (!slider.IsKeyboardFocusWithin && !slider.IsMouseCaptureWithin && !slider.IsStylusCaptureWithin))
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
}
