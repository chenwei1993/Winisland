using System.Net;
using System.Net.Http;
using WinIsland.Models;

namespace WinIsland.Services.Usage;

internal static class UsageHttp
{
    public static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static UsageHttp()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland/1.2 (+https://github.com/chenwei1993/Winisland)");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public static async Task<(HttpStatusCode Status, string Body)> GetAsync(
        string url,
        string apiKey,
        CancellationToken cancellationToken,
        string? workspaceId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            request.Headers.TryAddWithoutValidation("x-vela-workspace-id", workspaceId);
        }

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>OpenDesign cookie sessions cannot be sent as a Bearer token.</summary>
    public static async Task<(HttpStatusCode Status, string Body)> GetWithCookieAsync(
        string url,
        string cookie,
        CancellationToken cancellationToken,
        string? workspaceId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            request.Headers.TryAddWithoutValidation("x-vela-workspace-id", workspaceId);
        }

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    public static UsageFetchResult FromHttpStatus(HttpStatusCode status)
    {
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new UsageFetchResult(UsageStatus.Unauthorized, "API Key 无效或已过期", Array.Empty<UsageMetricData>(), null),
            HttpStatusCode.TooManyRequests =>
                new UsageFetchResult(UsageStatus.RateLimited, "请求过于频繁，稍后自动重试", Array.Empty<UsageMetricData>(), null),
            _ => new UsageFetchResult(UsageStatus.Error, $"HTTP {(int)status}",
                Array.Empty<UsageMetricData>(), null)
        };
    }
}
