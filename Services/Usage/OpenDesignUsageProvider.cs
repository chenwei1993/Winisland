using System.Globalization;
using System.Net;
using System.Text.Json;
using WinIsland.Models;

namespace WinIsland.Services.Usage;

/// <summary>
/// OpenDesign quota. Billing endpoints are not accessible with an inference API
/// key: they need either the Control Key (vela_ctrl_…) or a browser session
/// cookie, plus a workspace id. The preflight / coding-plan-usage responses
/// carry the 5-hour and weekly windows; the wallet is read separately when the
/// quota response does not include a balance.
/// </summary>
public sealed class OpenDesignUsageProvider : IUsageProvider
{
    private const string BaseUrl = "https://amr-api.open-design.ai/api/v1";

    public string Id => "opendesign";

    public string Name => "OpenDesign";

    public string ShortName => "OD";

    public string Glyph => "◆";

    public string? EnvironmentKey => "OPENDESIGN_CONTROL_KEY";

    public string Description => "5 小时 / 每周额度 · 钱包";

    public bool HasBalanceSettings => false;

    public string? ExtraSettingLabel => "工作区 ID";

    public string? ExtraSettingHint =>
        "支持 Control Key（vela_ctrl_…）或浏览器 Cookie：可粘贴完整 Cookie 头，也可只粘贴 session_token 的值；推理 Key（sk-amr_）不能查询额度。工作区 ID 可留空自动读取。";

    public string KeyPlaceholder => "粘贴 Control Key 或 Cookie";

    public TimeSpan Interval => TimeSpan.FromMinutes(2);

    public bool IsEnabled(AppSettings settings) =>
        settings.UsageEnabled && UsageProviderSettings.IsEnabled(settings, Id);

    public async Task<UsageFetchResult> FetchAsync(
        string apiKey,
        string? extra,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var credential = apiKey.Trim().Trim('"');
        var workspaceId = extra?.Trim() ?? string.Empty;

        if (credential.StartsWith("vela_ctrl", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchWithControlKeyAsync(credential, workspaceId, cancellationToken).ConfigureAwait(false);
        }

        if (credential.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
        {
            return new UsageFetchResult(UsageStatus.Unauthorized,
                "推理 API Key 不能查询 OpenDesign 额度，请使用 Control Key 或浏览器 Cookie",
                Array.Empty<UsageMetricData>(), null);
        }

        var cookie = BuildCookieHeader(credential);
        return cookie.Length == 0
            ? new UsageFetchResult(UsageStatus.NeedsKey, null, Array.Empty<UsageMetricData>(), null)
            : await FetchWithCookieAsync(cookie, workspaceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts a full Cookie header, a header with the "Cookie:" prefix, or just
    /// the bare session token value copied from DevTools.
    /// </summary>
    private static string BuildCookieHeader(string credential)
    {
        var value = credential.Trim();
        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..].Trim();
        }

        value = value.Replace("\r", " ").Replace("\n", " ");

        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.Contains("session_token", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var equals = value.IndexOf('=');
        if (equals > 0 && value[..equals].IndexOfAny([' ', ';', '\t']) < 0)
        {
            return value;
        }

        // Bare token value: send it under both the secure and plain better-auth
        // cookie names, since the site uses the prefixed one on HTTPS.
        return $"__Secure-better-auth.session_token={value}; better-auth.session_token={value}";
    }

    private async Task<UsageFetchResult> FetchWithControlKeyAsync(
        string key,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        if (workspaceId.Length == 0)
        {
            var (workspaceStatus, workspaceBody) = await UsageHttp
                .GetAsync($"{BaseUrl}/workspaces/current", key, cancellationToken)
                .ConfigureAwait(false);

            if (workspaceStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Unauthorized("Control Key 无效或已经失效");
            }

            if (workspaceStatus != HttpStatusCode.OK)
            {
                return UsageHttp.FromHttpStatus(workspaceStatus);
            }

            workspaceId = ParseWorkspaceId(workspaceBody);
            if (workspaceId.Length == 0)
            {
                return new UsageFetchResult(UsageStatus.Error, "无法读取工作区，请填写工作区 ID",
                    Array.Empty<UsageMetricData>(), null);
            }
        }

        var url = $"{BaseUrl}/workspaces/{Uri.EscapeDataString(workspaceId)}/billing/preflight";
        var (status, body) = await UsageHttp.GetAsync(url, key, cancellationToken, workspaceId)
            .ConfigureAwait(false);

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Unauthorized("Control Key 无效或没有该工作区权限");
        }

        if (status == HttpStatusCode.NotFound)
        {
            return new UsageFetchResult(UsageStatus.Error, "找不到该工作区，请检查工作区 ID",
                Array.Empty<UsageMetricData>(), null);
        }

        if (status != HttpStatusCode.OK)
        {
            return UsageHttp.FromHttpStatus(status);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var preflight = root.TryGetProperty("preflight", out var envelope) &&
                            envelope.ValueKind == JsonValueKind.Object
                ? envelope
                : root;

            if (!preflight.TryGetProperty("codingPlan", out var plan) ||
                plan.ValueKind != JsonValueKind.Object)
            {
                return new UsageFetchResult(UsageStatus.Error, "响应中没有套餐额度字段",
                    Array.Empty<UsageMetricData>(), ReadBalance(preflight));
            }

            var balance = ReadBalance(preflight);
            if (balance is null)
            {
                balance = await FetchWalletAsync(
                        $"{BaseUrl}/workspaces/{Uri.EscapeDataString(workspaceId)}/wallet/balance",
                        key,
                        cookieMode: false,
                        workspaceId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return BuildResult(plan, balance);
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }
    }

    private async Task<UsageFetchResult> FetchWithCookieAsync(
        string cookie,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        if (workspaceId.Length == 0)
        {
            var (workspaceStatus, workspaceBody) = await UsageHttp
                .GetWithCookieAsync($"{BaseUrl}/workspaces/current", cookie, cancellationToken)
                .ConfigureAwait(false);

            if (workspaceStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Unauthorized("Cookie 已过期或缺少权限，请重新复制");
            }

            if (workspaceStatus != HttpStatusCode.OK)
            {
                return UsageHttp.FromHttpStatus(workspaceStatus);
            }

            workspaceId = ParseWorkspaceId(workspaceBody);
            if (workspaceId.Length == 0)
            {
                return new UsageFetchResult(UsageStatus.Error, "无法读取工作区，请填写工作区 ID",
                    Array.Empty<UsageMetricData>(), null);
            }
        }

        var url = $"{BaseUrl}/workspaces/{Uri.EscapeDataString(workspaceId)}/billing/coding-plan-usage";
        var (status, body) = await UsageHttp.GetWithCookieAsync(url, cookie, cancellationToken, workspaceId)
            .ConfigureAwait(false);

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Unauthorized("Cookie 已过期或缺少该工作区权限，请重新复制");
        }

        if (status == HttpStatusCode.NotFound)
        {
            return new UsageFetchResult(UsageStatus.Error, "找不到该工作区，请检查工作区 ID",
                Array.Empty<UsageMetricData>(), null);
        }

        if (status != HttpStatusCode.OK)
        {
            return UsageHttp.FromHttpStatus(status);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var plan = root.TryGetProperty("codingPlan", out var nested) &&
                       nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            var balance = await FetchWalletAsync(
                    $"{BaseUrl}/workspaces/{Uri.EscapeDataString(workspaceId)}/wallet/balance",
                    cookie,
                    cookieMode: true,
                    workspaceId,
                    cancellationToken)
                .ConfigureAwait(false);

            return BuildResult(plan, balance);
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }
    }

    private static async Task<UsageBalanceData?> FetchWalletAsync(
        string url,
        string credential,
        bool cookieMode,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        var (status, body) = cookieMode
            ? await UsageHttp.GetWithCookieAsync(url, credential, cancellationToken, workspaceId)
                .ConfigureAwait(false)
            : await UsageHttp.GetAsync(url, credential, cancellationToken, workspaceId)
                .ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadBalance(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UsageFetchResult BuildResult(JsonElement plan, UsageBalanceData? balance)
    {
        var eligible = plan.TryGetProperty("eligible", out var eligibleElement) &&
                       eligibleElement.ValueKind == JsonValueKind.True;
        var tier = NormalizeTier(ReadString(plan, "tier"));
        var metrics = new List<UsageMetricData>();

        if (plan.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Array)
        {
            foreach (var window in windows.EnumerateArray())
            {
                if (window.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var duration = ReadInt(window, "durationSeconds");
                var used = ReadDecimal(window, "usedCredits");
                var limit = ReadDecimal(window, "limitCredits");
                if (duration is null or <= 0 || limit is null or <= 0 || used is null)
                {
                    continue;
                }

                var percent = Math.Clamp((double)(used.Value / limit.Value) * 100d, 0, 100);
                var reset = ParseDate(ReadString(window, "resetsAt"));
                metrics.Add(new UsageMetricData(
                    $"window-{duration}",
                    WindowLabel(duration.Value),
                    percent,
                    reset));
            }
        }

        if (!eligible && metrics.Count == 0)
        {
            return new UsageFetchResult(UsageStatus.NoPlan, "账户没有生效的 OpenDesign 套餐",
                Array.Empty<UsageMetricData>(), balance);
        }

        if (metrics.Count == 0)
        {
            return new UsageFetchResult(UsageStatus.Error, "没有可用的额度窗口",
                Array.Empty<UsageMetricData>(), balance, tier);
        }

        return new UsageFetchResult(UsageStatus.Ok, null, metrics, balance, tier);
    }

    private static UsageFetchResult InvalidResponse() =>
        new(UsageStatus.Error, "响应格式无法识别", Array.Empty<UsageMetricData>(), null);

    private static UsageFetchResult Unauthorized(string message) =>
        new(UsageStatus.Unauthorized, message, Array.Empty<UsageMetricData>(), null);

    private static UsageBalanceData? ReadBalance(JsonElement element)
    {
        var balance = ReadDecimal(element, "balanceUsd");
        return balance is null
            ? null
            : new UsageBalanceData(Math.Max(0, balance.Value), 0m, 0m, "USD", true, HasBreakdown: false);
    }

    private static string WindowLabel(int durationSeconds) => durationSeconds switch
    {
        18000 => "5 小时",
        604800 => "本周",
        _ when durationSeconds % 86400 == 0 => $"{durationSeconds / 86400} 天",
        _ when durationSeconds % 3600 == 0 => $"{durationSeconds / 3600} 小时",
        _ => $"{Math.Round(durationSeconds / 3600d, 1)} 小时"
    };

    private static string? NormalizeTier(string? tier) => tier?.ToLowerInvariant() switch
    {
        "go" => "Go",
        "plus" => "Plus",
        "pro" => "Pro",
        "max" => "Max",
        _ => null
    };

    private static string ParseWorkspaceId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            foreach (var name in new[] { "workspaceId", "activeWorkspaceId" })
            {
                if (ReadString(root, name) is { Length: > 0 } direct)
                {
                    return direct;
                }
            }

            if (root.TryGetProperty("workspace", out var workspace) &&
                workspace.ValueKind == JsonValueKind.Object)
            {
                if (ReadString(workspace, "workspaceId") is { Length: > 0 } nested)
                {
                    return nested;
                }

                if (ReadString(workspace, "id") is { Length: > 0 } id)
                {
                    return id;
                }
            }

            foreach (var name in new[] { "items", "workspaces" })
            {
                if (!root.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        ReadString(item, "workspaceId") is { Length: > 0 } itemId)
                    {
                        return itemId;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to "no workspace".
        }

        return string.Empty;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static decimal? ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? ParseDate(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }
}
