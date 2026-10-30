using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using WinIsland.Models;

namespace WinIsland.Services;

/// <summary>
/// Receives notifications / commands over the <c>\\.\pipe\WinIsland</c> named pipe.
/// Lines are JSON objects, or plain <c>title|message</c> text.
/// </summary>
public sealed class NotificationPipeServer : IDisposable
{
    public const string PipeName = "WinIsland";
    public const string DisplayName = @"\\.\pipe\WinIsland";

    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;

    public void Start()
    {
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cancellation.Token);

                using var reader = new StreamReader(server, new UTF8Encoding(false));
                while (!_cancellation.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cancellation.Token);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var command = IslandCommand.Parse(line);
                    if (command is not null)
                    {
                        Dispatch(command);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(300, _cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static void Dispatch(IslandCommand command)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        application.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (application.MainWindow is MainWindow island)
            {
                island.HandleCommand(command);
            }
        }));
    }

    public static bool TrySendCommand(string line, int timeoutMs = 900)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(line);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _loop?.Wait(500);
        }
        catch
        {
            // Ignore shutdown races.
        }

        _cancellation.Dispose();
    }
}
