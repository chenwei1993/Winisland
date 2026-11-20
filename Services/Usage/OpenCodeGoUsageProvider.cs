using System.Globalization;
using System.Net;
using System.Text.Json;
using WinIsland.Models;

namespace WinIsland.Services.Usage;

/// <summary>
/// OpenCode Go exposes three quota windows through the same endpoint the web
/// console uses: <c>GET https://opencode.ai/zen/go/v1/usage</c>. Each window
/// reports the percentage already used and when it resets.
/// </summary>
public sealed class OpenCodeGoUsageProvider : IUsageProvider
{
    private const string UsageUrl = "https://opencode.ai/zen/go/v1/usage";

    private static readonly (string Key, string Label)[] Windows =
    [
        ("rolling", "5 小时"),
        ("weekly", "本周"),
        ("monthly", "本月")
    ];

    public string Id => "opencode-go";

    public string Name => "OpenCode Go";

    public string ShortName => "Go";

    public string Glyph => "⚡";

    public string? EnvironmentKey => "OPENCODE_GO_KEY";

    public string Description => "5 小时 / 每周 / 每月额度";

    public bool HasBalanceSettings => false;

    public TimeSpan Interval => TimeSpan.FromMinutes(2);

    public bool IsEnabled(AppSettings settings) =>
        settings.UsageEnabled && UsageProviderSettings.IsEnabled(settings, Id);

    public async Task<UsageFetchResult> FetchAsync(string apiKey, string? extra, AppSettings settings, CancellationToken cancellationToken)
    {
        var (status, body) = await UsageHttp.GetAsync(UsageUrl, apiKey, cancellationToken).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            return UsageHttp.FromHttpStatus(status);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return new UsageFetchResult(UsageStatus.Error, "响应中没有用量字段", Array.Empty<UsageMetricData>(), null);
            }

            var metrics = new List<UsageMetricData>();
            foreach (var (key, label) in Windows)
            {
                if (!usage.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (ReadDouble(window, "percent") is not { } percent)
                {
                    continue;
                }

                DateTimeOffset? resetAt = null;
                if (ReadString(window, "resetsAt") is { } resetText &&
                    DateTimeOffset.TryParse(resetText, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    resetAt = parsed;
                }

                metrics.Add(new UsageMetricData(key, label, Math.Clamp(percent, 0, 100), resetAt));
            }

            if (metrics.Count == 0)
            {
                return new UsageFetchResult(UsageStatus.Error, "没有可用的额度窗口", Array.Empty<UsageMetricData>(), null);
            }

            return UsageFetchResult.Ok(metrics);
        }
        catch (JsonException)
        {
            return new UsageFetchResult(UsageStatus.Error, "响应格式无法识别", Array.Empty<UsageMetricData>(), null);
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
