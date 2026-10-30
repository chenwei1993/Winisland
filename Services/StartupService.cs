using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace WinIsland.Services;

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinIsland";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var command = BuildLaunchCommand();
                if (!string.IsNullOrWhiteSpace(command))
                {
                    key.SetValue(ValueName, command);
                }
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch
        {
            // Ignore registry failures (policy-locked machines).
        }
    }

    /// <summary>
    /// Prefer launching through dotnet.exe for framework-dependent builds, because
    /// the app host cannot always locate a user-local .NET installation.
    /// </summary>
    private static string BuildLaunchCommand()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var executable = Environment.ProcessPath;
        var assembly = Path.Combine(baseDirectory, "WinIsland.dll");
        var runtimeConfig = Path.Combine(baseDirectory, "WinIsland.runtimeconfig.json");
        var selfContained = File.Exists(Path.Combine(baseDirectory, "hostfxr.dll"));

        if (!selfContained && File.Exists(assembly) && File.Exists(runtimeConfig))
        {
            var dotnet = FindDotnet();
            if (dotnet is not null)
            {
                return $"\"{dotnet}\" \"{assembly}\"";
            }
        }

        return string.IsNullOrWhiteSpace(executable) ? string.Empty : $"\"{executable}\"";
    }

    private static string? FindDotnet()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        try
        {
            var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
            var directory = new DirectoryInfo(runtimeDirectory);
            var dotnetRoot = directory.Parent?.Parent?.Parent?.FullName;
            if (dotnetRoot is not null)
            {
                var candidate = Path.Combine(dotnetRoot, "dotnet.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Ignore.
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var item in path.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(item.Trim(), "dotnet.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }
}
