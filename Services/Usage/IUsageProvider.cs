using WinIsland.Models;

namespace WinIsland.Services.Usage;

public sealed record UsageProviderInfo(
    string Id,
    string Name,
    string Glyph,
    string? EnvironmentKey,
    string Description,
    bool HasBalanceSettings,
    string? ExtraLabel,
    string? ExtraHint,
    string KeyPlaceholder);

public sealed record UsageMetricData(string Key, string Label, double UsedPercent, DateTimeOffset? ResetAt);

public sealed record UsageBalanceData(
    decimal Total,
    decimal Granted,
    decimal ToppedUp,
    string Currency,
    bool Available,
    bool HasBreakdown = true);

public sealed record UsageFetchResult(
    UsageStatus Status,
    string? Error,
    IReadOnlyList<UsageMetricData> Metrics,
    UsageBalanceData? Balance,
    string? Tier = null)
{
    public static UsageFetchResult Ok(
        IReadOnlyList<UsageMetricData>? metrics = null,
        UsageBalanceData? balance = null) =>
        new(UsageStatus.Ok, null, metrics ?? Array.Empty<UsageMetricData>(), balance);
}

/// <summary>
/// One source of usage data. Adding a provider only needs an implementation of
/// this interface and a line in <see cref="UsageService"/>; the island chip,
/// usage pane, thresholds and alerts use the shared model.
/// </summary>
public interface IUsageProvider
{
    string Id { get; }

    string Name { get; }

    /// <summary>Short label used by the island chip, e.g. "Go".</summary>
    string ShortName { get; }

    string Glyph { get; }

    /// <summary>Environment variable checked when no key is saved in settings.</summary>
    string? EnvironmentKey { get; }

    /// <summary>One line shown under the provider name in the settings window.</summary>
    string Description { get; }

    /// <summary>Shows the DeepSeek-specific budget / low balance fields.</summary>
    bool HasBalanceSettings { get; }

    /// <summary>Optional extra setting label (e.g. "工作区 ID") shown in the settings window.</summary>
    string? ExtraSettingLabel => null;

    string? ExtraSettingHint => null;

    /// <summary>Placeholder shown in the settings window's credential box.</summary>
    string KeyPlaceholder => "粘贴 API Key";

    TimeSpan Interval { get; }

    bool IsEnabled(AppSettings settings);

    Task<UsageFetchResult> FetchAsync(
        string apiKey,
        string? extra,
        AppSettings settings,
        CancellationToken cancellationToken);
}
