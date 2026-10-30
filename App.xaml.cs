using System.Text.Json;
using System.Threading;
using System.Windows;
using WinIsland.Services;

namespace WinIsland;

public partial class App : Application
{
    private const string MutexName = @"Local\WinIsland.SingleInstance";

    private Mutex? _mutex;
    private TrayService? _tray;

    public static ServiceHub Services { get; private set; } = null!;
    public static MainWindow? Island { get; private set; }
    public static bool IsExiting { get; private set; }
    public static string? PendingCommand { get; set; }

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true;

            if (MainWindow is null)
            {
                Shutdown(1);
            }
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var command = BuildCommand(e.Args);

        bool createdNew;
        _mutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew)
        {
            // Another island is already running: forward the command and quit.
            NotificationPipeServer.TrySendCommand(command ?? "{\"action\":\"show\"}");
            Shutdown();
            return;
        }

        PendingCommand = command;

        Services = new ServiceHub();
        Services.Start();

        var window = new MainWindow();
        Island = window;
        MainWindow = window;
        window.Show();

        _tray = new TrayService();
        _tray.ToggleRequested += () => Island?.ToggleFromTray();
        _tray.SettingsRequested += () => Island?.ShowSettings();
        _tray.TestNotificationRequested += () => Island?.ShowTestNotification();
        _tray.ExitRequested += ExitApp;
    }

    public static void ExitApp()
    {
        IsExiting = true;
        Current?.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Services?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void LogCrash(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinIsland");
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(directory, "crash.log"),
                $"[{DateTime.Now:O}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }

    private static string? BuildCommand(string[] args)
    {        if (args.Length == 0)
        {
            return null;
        }

        var first = args[0].Trim().ToLowerInvariant();
        switch (first)
        {
            case "--show":
            case "show":
                return "{\"action\":\"show\"}";

            case "--collapse":
            case "collapse":
                return "{\"action\":\"collapse\"}";

            case "--notify":
            case "-n":
            case "notify":
            {
                var title = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : "通知";
                var message = args.Length > 2 ? args[2] : string.Empty;
                var app = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]) ? args[3] : "命令行";
                return JsonSerializer.Serialize(new { action = "notify", title, message, app, kind = "info" });
            }

            case "--timer":
            case "-t":
            case "timer":
            {
                var minutes = 5d;
                if (args.Length > 1)
                {
                    if (!double.TryParse(args[1], out minutes))
                    {
                        minutes = 5;
                    }
                }

                minutes = Math.Clamp(minutes, 0.1, 600);
                var title = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : "计时";
                return JsonSerializer.Serialize(new { action = "timer", minutes, title });
            }

            case "--shelf":
            case "shelf":
            {
                var files = args.Skip(1).Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
                if (files.Length == 0)
                {
                    return "{\"action\":\"shelf\",\"files\":[]}";
                }

                return JsonSerializer.Serialize(new { action = "shelf", files });
            }

            case "--progress":
            case "progress":
            {
                var value = 0d;
                if (args.Length > 1 && !double.TryParse(args[1], out value))
                {
                    value = 0;
                }

                if (value > 1)
                {
                    value /= 100;
                }

                value = Math.Clamp(value, 0, 1);
                var title = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : "进度";
                var message = args.Length > 3 ? args[3] : string.Empty;
                var seconds = 5d;
                if (args.Length > 4 && double.TryParse(args[4], out var parsedSeconds))
                {
                    seconds = parsedSeconds;
                }

                return JsonSerializer.Serialize(new
                {
                    action = "progress",
                    title,
                    message,
                    progress = value,
                    seconds = Math.Clamp(seconds, 1.5, 30)
                });
            }
        }

        return null;
    }
}
