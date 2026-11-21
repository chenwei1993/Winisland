using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace WinIsland.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Downloaded,
    Installing,
    Failed
}

/// <summary>A newer build published on GitHub Releases.</summary>
public sealed class UpdateInfo
{
    public string Tag { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string PageUrl { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public long Size { get; init; }

    public string SizeText => Size > 0 ? $"{Size / 1024d / 1024d:0} MB" : "未知大小";
}

/// <summary>
/// Checks GitHub Releases for a newer WinIsland build, downloads it and swaps
/// the running executable through a small PowerShell helper, so the island can
/// update itself without opening a browser.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string ReleaseApi = "https://api.github.com/repos/chenwei1993/Winisland/releases/latest";
    private const string ReleasePage = "https://github.com/chenwei1993/Winisland/releases/latest";

    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    private static readonly Version AssemblyVersion =
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);

    private readonly SettingsService _settings;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };

    private bool _started;
    private bool _disposed;
    private string? _notifiedVersion;

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"WinIsland/{CurrentVersionText}");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Version of the running assembly, trimmed to major.minor.build.</summary>
    public static Version CurrentVersion { get; } =
        new(AssemblyVersion.Major, AssemblyVersion.Minor, Math.Max(AssemblyVersion.Build, 0));

    public string CurrentVersionText => CurrentVersion.ToString();

    public UpdateState State { get; private set; } = UpdateState.Idle;

    public UpdateInfo? Latest { get; private set; }

    /// <summary>Download progress between 0 and 1 while <see cref="UpdateState.Downloading"/>.</summary>
    public double Progress { get; private set; }

    public string? Message { get; private set; }

    public DateTime? LastChecked { get; private set; }

    public string? DownloadedPath { get; private set; }

    /// <summary>Human readable name of the mirror or source used for the download.</summary>
    public string? SourceText { get; private set; }

    public string PageUrl => string.IsNullOrWhiteSpace(Latest?.PageUrl) ? ReleasePage : Latest!.PageUrl;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    public event Action? StateChanged;

    /// <summary>Raised once per session when a newer version is found.</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        ApplyAutoCheckSetting();
    }

    /// <summary>(Re)starts the background check timer according to the setting.</summary>
    public void ApplyAutoCheckSetting()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();

        if (!_settings.Current.AutoCheckUpdates)
        {
            return;
        }

        _timer.Interval = TimeSpan.FromSeconds(10);
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        _timer.Interval = AutoCheckInterval;
        _timer.Start();
        await CheckAsync().ConfigureAwait(true);
    }

    /// <summary>Queries the latest GitHub release and compares it with this build.</summary>
    public async Task CheckAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        Message = null;
        SetState(UpdateState.Checking);

        try
        {
            using var response = await _client.GetAsync(ReleaseApi).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                SetState(UpdateState.Failed, $"检查失败：GitHub 返回 {(int)response.StatusCode}");
                return;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
            var pageUrl = root.TryGetProperty("html_url", out var pageElement) ? pageElement.GetString() ?? "" : "";
            var version = ParseVersion(tag);
            LastChecked = DateTime.Now;

            var downloadUrl = string.Empty;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (!string.Equals(name, "WinIsland.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                        ? urlElement.GetString() ?? ""
                        : "";
                    if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed))
                    {
                        size = parsed;
                    }

                    break;
                }
            }

            if (version is null || version <= CurrentVersion)
            {
                Latest = null;
                DownloadedPath = null;
                SetState(UpdateState.UpToDate);
                return;
            }

            Latest = new UpdateInfo
            {
                Tag = tag,
                Version = version.ToString(),
                PageUrl = pageUrl,
                DownloadUrl = downloadUrl,
                Size = size
            };
            DownloadedPath = null;
            SetState(UpdateState.Available);

            if (!string.Equals(_notifiedVersion, Latest.Version, StringComparison.OrdinalIgnoreCase))
            {
                _notifiedVersion = Latest.Version;
                var info = Latest;
                Post(() => UpdateAvailable?.Invoke(info));
            }
        }
        catch (Exception)
        {
            SetState(UpdateState.Failed, "检查失败，请确认网络可以访问 GitHub");
        }
    }

    /// <summary>Downloads the release asset next to the app data and verifies its size.</summary>
    public async Task<bool> DownloadAsync()
    {
        if (_disposed || State == UpdateState.Downloading)
        {
            return false;
        }

        var info = Latest;
        if (info is null)
        {
            SetState(UpdateState.Failed, "没有可下载的新版本，请先检查更新");
            return false;
        }

        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            SetState(UpdateState.Failed, "此版本没有可下载的安装包，请打开发布页");
            return false;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland", "update");
        var file = Path.Combine(directory, $"WinIsland-{info.Version}.exe");
        var partial = file + ".part";

        Progress = 0;
        SourceText = null;
        SetState(UpdateState.Downloading);

        try
        {
            Directory.CreateDirectory(directory);

            var source = await DownloadFromBestSourceAsync(info, partial).ConfigureAwait(false);
            if (source is null)
            {
                SetState(UpdateState.Failed, "下载失败，请检查网络或稍后重试");
                return false;
            }

            if (!VerifyDownloadedFile(partial, info))
            {
                // A mirror may have served something else. Drop the file and try
                // one more time from scratch before giving up.
                TryDelete(partial);
                source = await DownloadFromBestSourceAsync(info, partial).ConfigureAwait(false);

                if (source is null || !VerifyDownloadedFile(partial, info))
                {
                    TryDelete(partial);
                    SetState(UpdateState.Failed, "下载的文件校验失败，请重试");
                    return false;
                }
            }

            if (File.Exists(file))
            {
                File.Delete(file);
            }

            File.Move(partial, file);
            DownloadedPath = file;
            SourceText = source;
            Progress = 1;
            SetState(UpdateState.Downloaded);
            return true;
        }
        catch (Exception)
        {
            TryDelete(partial);
            SetState(UpdateState.Failed, "下载失败，请检查网络后重试");
            return false;
        }
    }

    private sealed record DownloadSource(string Name, string Url);

    // Public GitHub proxies that speed release downloads up for networks where
    // github.com itself is slow. They are only used for the exe; the version
    // metadata still comes straight from the GitHub API.
    private static readonly (string Name, string Base)[] Mirrors =
    {
        ("ghproxy.net", "https://ghproxy.net/"),
        ("gh-proxy.com", "https://gh-proxy.com/"),
        ("ghfast.top", "https://ghfast.top/"),
        ("github.moeyy.xyz", "https://github.moeyy.xyz/")
    };

    private async Task<string?> DownloadFromBestSourceAsync(UpdateInfo info, string partial)
    {
        foreach (var source in await RankSourcesAsync(info).ConfigureAwait(false))
        {
            try
            {
                SourceText = source.Name;
                Post(() => StateChanged?.Invoke());

                if (await DownloadFromAsync(source, info, partial).ConfigureAwait(false))
                {
                    return source.Name;
                }
            }
            catch
            {
                // Fall through to the next source; the partial file is kept so
                // the next attempt can resume where this one stopped.
            }
        }

        return null;
    }

    /// <summary>
    /// Probes mirrors and the official URL for a couple of seconds each and
    /// returns them ordered from fastest to slowest.
    /// </summary>
    private async Task<List<DownloadSource>> RankSourcesAsync(UpdateInfo info)
    {
        var candidates = new List<DownloadSource>();
        foreach (var mirror in Mirrors)
        {
            candidates.Add(new DownloadSource($"加速 {mirror.Name}", mirror.Base + info.DownloadUrl));
        }

        candidates.Add(new DownloadSource("GitHub 官方", info.DownloadUrl));

        var ranked = new List<(DownloadSource Source, double Speed)>();
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var speed = await ProbeSourceAsync(candidate, info).ConfigureAwait(false);
            ranked.Add((candidate, speed));

            if (speed < 3_000_000 && clock.Elapsed <= TimeSpan.FromSeconds(6))
            {
                continue;
            }

            for (var rest = i + 1; rest < candidates.Count; rest++)
            {
                ranked.Add((candidates[rest], 0));
            }

            break;
        }

        return ranked
            .OrderByDescending(entry => entry.Speed)
            .Select(entry => entry.Source)
            .ToList();
    }

    private async Task<double> ProbeSourceAsync(DownloadSource source, UpdateInfo info)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var response = await _client
                .GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            var length = response.Content.Headers.ContentLength ?? 0;
            if (info.Size > 0 && length > 0 && Math.Abs(length - info.Size) > 4096)
            {
                return 0;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[65536];
            var clock = Stopwatch.StartNew();
            long received = 0;

            while (clock.Elapsed < TimeSpan.FromSeconds(2) && received < 1_500_000)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                received += read;
            }

            return received / Math.Max(clock.Elapsed.TotalSeconds, 0.05);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<bool> DownloadFromAsync(DownloadSource source, UpdateInfo info, string partial)
    {
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (info.Size > 0 && existing >= info.Size)
        {
            existing = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (existing > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);

        long start;
        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From is { } from && from != existing)
            {
                return false;
            }

            start = existing;
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            // The source ignored the range request: start the file over.
            start = 0;
            existing = 0;
        }
        else
        {
            return false;
        }

        var total = info.Size;
        if (total <= 0)
        {
            total = response.Content.Headers.ContentRange?.Length ??
                    response.Content.Headers.ContentLength ?? 0;
        }

        if (total > 0 && start > 0 && start >= total)
        {
            return true;
        }

        var lastReport = -1d;
        var clock = Stopwatch.StartNew();
        var windowTime = TimeSpan.Zero;
        var windowBytes = 0L;
        var slowChecks = 0;
        long received = start;

        ReportProgress(total <= 0 ? 0 : (double)received / total);

        await using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var output = new FileStream(partial, existing > 0 ? FileMode.Open : FileMode.Create,
                         FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            if (existing > 0)
            {
                output.Seek(existing, SeekOrigin.Begin);
            }

            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                received += read;

                // Drop a source that crawls: roughly two 2-second windows under 40 KB/s.
                var elapsed = clock.Elapsed;
                if ((elapsed - windowTime).TotalSeconds >= 2)
                {
                    var speed = (received - start - windowBytes) / (elapsed - windowTime).TotalSeconds;
                    windowTime = elapsed;
                    windowBytes = received - start;
                    slowChecks = speed < 40 * 1024 ? slowChecks + 1 : 0;

                    if (slowChecks >= 2)
                    {
                        return false;
                    }
                }

                if (total <= 0)
                {
                    continue;
                }

                ReportProgress(Math.Clamp((double)received / total, 0, 1), ref lastReport);
            }
        }

        return total <= 0 || received == total;
    }

    private void ReportProgress(double value, ref double lastReport)
    {
        if (value - lastReport < 0.005 && value < 1)
        {
            return;
        }

        lastReport = value;
        ReportProgress(value);
    }

    private void ReportProgress(double value)
    {
        Progress = Math.Clamp(value, 0, 1);
        Post(() => StateChanged?.Invoke());
    }

    private static bool VerifyDownloadedFile(string path, UpdateInfo info)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(version.ProductName, "WinIsland", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var product = version.ProductVersion ?? string.Empty;
            return string.IsNullOrEmpty(info.Version) ||
                   product.StartsWith(info.Version, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a helper that waits for this process to exit, replaces the exe and
    /// relaunches it. Returns false with a reason when the swap cannot be done.
    /// </summary>
    public bool RestartAndInstall(out string error)
    {
        error = string.Empty;

        var executable = Environment.ProcessPath;
        if (executable is null ||
            !string.Equals(Path.GetFileName(executable), "WinIsland.exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "当前是开发调试运行，请打开发布页手动下载";
            return false;
        }

        var file = DownloadedPath;
        if (file is null || !File.Exists(file))
        {
            error = "安装文件不存在，请重新下载";
            return false;
        }

        var directory = Path.GetDirectoryName(executable);
        if (string.IsNullOrEmpty(directory))
        {
            error = "无法定位程序目录，请打开发布页手动下载";
            return false;
        }

        try
        {
            var probe = Path.Combine(directory, $".winisland-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch
        {
            error = "程序目录不可写，请把 WinIsland 放到可写目录或打开发布页手动下载";
            return false;
        }

        try
        {
            var script = Path.Combine(Path.GetTempPath(), $"WinIsland-update-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(script, BuildUpdaterScript(executable, file), new UTF8Encoding(true));

            var startInfo = new ProcessStartInfo(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo);
            SetState(UpdateState.Installing);
            return true;
        }
        catch (Exception)
        {
            error = "无法启动更新程序，请打开发布页手动下载";
            return false;
        }
    }

    internal static Version? ParseVersion(string tag)
    {
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0)
        {
            text = text[..dash];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }

    internal static string BuildUpdaterScript(string target, string update)
    {
        var pid = Environment.ProcessId;

        return $$"""
$ErrorActionPreference = 'SilentlyContinue'
$target = '{{Escape(target)}}'
$update = '{{Escape(update)}}'
$backup = "$target.old"

# Wait for the island to exit before touching its executable.
for ($i = 0; $i -lt 300; $i++) {
    if (-not (Get-Process -Id {{pid}} -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 200
}
Start-Sleep -Milliseconds 400

try {
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    Move-Item -LiteralPath $target -Destination $backup -Force -ErrorAction Stop
    Move-Item -LiteralPath $update -Destination $target -Force -ErrorAction Stop
    Start-Process -FilePath $target -WorkingDirectory (Split-Path -LiteralPath $target)
    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath $backup -Force
} catch {
    if (Test-Path -LiteralPath $backup) {
        Move-Item -LiteralPath $backup -Destination $target -Force
    }

    if (-not (Get-Process -Name 'WinIsland' -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $target -WorkingDirectory (Split-Path -LiteralPath $target)
    }
}

Remove-Item -LiteralPath $PSCommandPath -Force
""";

        static string Escape(string value) => value.Replace("'", "''");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Leftover partial files are harmless.
        }
    }

    private void SetState(UpdateState state, string? message = null)
    {
        State = state;
        Message = message;
        if (state != UpdateState.Downloading)
        {
            Progress = state == UpdateState.Downloaded ? 1 : 0;
        }

        Post(() => StateChanged?.Invoke());
    }

    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _client.Dispose();
    }
}
