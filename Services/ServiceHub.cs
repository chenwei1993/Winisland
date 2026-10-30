namespace WinIsland.Services;

public sealed class ServiceHub : IDisposable
{
    public SettingsService Settings { get; }
    public StartupService Startup { get; }
    public AudioService Audio { get; }
    public BrightnessService Brightness { get; }
    public PowerService Power { get; }
    public SystemMonitor Monitor { get; }
    public ClipboardWatcher Clipboard { get; }
    public ShelfService Shelf { get; }
    public TimerService Timers { get; }
    public MediaService Media { get; }
    public MicCameraWatcher Sensors { get; }
    public ForegroundAppWatcher Apps { get; }
    public SystemNotificationWatcher SystemNotifications { get; }
    public NotificationPipeServer Pipes { get; }

    public ServiceHub()
    {
        Settings = new SettingsService();
        Settings.Load();

        Startup = new StartupService();
        Settings.Current.StartWithWindows = Startup.IsEnabled();

        Audio = new AudioService();
        Brightness = new BrightnessService();
        Power = new PowerService();
        Monitor = new SystemMonitor();
        Clipboard = new ClipboardWatcher();
        Shelf = new ShelfService(Settings);
        Timers = new TimerService();
        Media = new MediaService();
        Sensors = new MicCameraWatcher();
        Apps = new ForegroundAppWatcher();
        SystemNotifications = new SystemNotificationWatcher();
        Pipes = new NotificationPipeServer();
    }

    public void Start()
    {
        Shelf.Load();
        Clipboard.Enabled = Settings.Current.RecordClipboard;

        Audio.Start();
        Brightness.Start();
        Power.Start();
        Monitor.Start();
        Sensors.Start();
        Timers.Start();
        Apps.Start();
        SystemNotifications.Enabled = Settings.Current.ShowSystemNotifications;
        SystemNotifications.Start();
        Pipes.Start();
        _ = Media.StartAsync();
    }

    public void Dispose()
    {
        Pipes.Dispose();
        Media.Dispose();
        SystemNotifications.Dispose();
        Apps.Dispose();
        Sensors.Dispose();
        Timers.Dispose();
        Clipboard.Dispose();
        Monitor.Dispose();
        Power.Dispose();
        Brightness.Dispose();
        Audio.Dispose();
    }
}
