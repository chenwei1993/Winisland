using System.Globalization;
using System.Net;
using System.Text.Json;
using WinIsland.Models;

namespace WinIsland.Services.Usage;

/// <summary>
/// DeepSeek's public API only exposes the account balance
/// (<c>GET /user/balance</c>); there is no period-to-date spend endpoint.
/// When a monthly budget is configured the balance also becomes a percentage.
/// </summary>
public sealed class DeepSeekUsageProvider : IUsageProvider
{
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";

    public string Id => "deepseek";

    public string Name => "DeepSeek";

    public string ShortName => "DS";

    public string Glyph => "¥";

    public string? EnvironmentKey => "DEEPSEEK_API_KEY";

    public string Description => "余额 · 今日消耗";

    public bool HasBalanceSettings => true;

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public bool IsEnabled(AppSettings settings) =>
        settings.UsageEnabled && UsageProviderSettings.IsEnabled(settings, Id);

    public async Task<UsageFetchResult> FetchAsync(string apiKey, AppSettings settings, CancellationToken cancellationToken)
    {
        var (status, body) = await UsageHttp.GetAsync(BalanceUrl, apiKey, cancellationToken).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            return UsageHttp.FromHttpStatus(status);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var available = !root.TryGetProperty("is_available", out var availableElement) ||
                            availableElement.ValueKind != JsonValueKind.False;

            if (!root.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array)
            {
                return new UsageFetchResult(UsageStatus.Error, "响应格式无法识别", Array.Empty<UsageMetricData>(), null);
            }

            JsonElement? picked = null;
            foreach (var info in infos.EnumerateArray())
            {
                picked ??= info;
                var currency = ReadString(info, "currency");
                if (string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase))
                {
                    picked = info;
                    break;
                }
            }

            if (picked is not { } balanceInfo)
            {
                return new UsageFetchResult(UsageStatus.Error, "账户没有余额信息", Array.Empty<UsageMetricData>(), null);
            }

            var currencyText = ReadString(balanceInfo, "currency") ?? "CNY";
            var total = ReadDecimal(balanceInfo, "total_balance");
            var granted = ReadDecimal(balanceInfo, "granted_balance");
            var toppedUp = ReadDecimal(balanceInfo, "topped_up_balance");

            var balance = new UsageBalanceData(total, granted, toppedUp, currencyText, available);
            var metrics = new List<UsageMetricData>();

            if (settings.DeepSeekBudget > 0 && total >= 0)
            {
                var used = (1d - (double)(total / settings.DeepSeekBudget)) * 100d;
                metrics.Add(new UsageMetricData("budget", "本月预算", Math.Clamp(used, 0, 100), null));
            }

            return UsageFetchResult.Ok(metrics, balance);
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

    private static decimal ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0m;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }
}
