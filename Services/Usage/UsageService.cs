using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Windows.Threading;
using WinIsland.Models;

namespace WinIsland.Services.Usage;

/// <summary>
/// Polls the configured AI usage providers, keeps one card per provider for the
/// usage pane, and projects the worst window into a tiny island chip. Provider
/// failures (bad key, rate limit, offline) never surface as real 0% data.
/// </summary>
public sealed class UsageService : INotifyPropertyChanged, IDisposable
{
    private readonly SettingsService _settings;
    private readonly UsageSecretStore _secrets;
    private readonly UsageStateStore _state;
    private readonly Dispatcher _dispatcher;
    private readonly List<IUsageProvider> _providers =
    [
        new DeepSeekUsageProvider(),
        new OpenCodeGoUsageProvider(),
        new OpenDesignUsageProvider()
    ];

    private readonly Dictionary<string, UsageProviderCard> _cards = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _nextFetch = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cancellation;
    private bool _started;
    private bool _disposed;

    private bool _hasChip;
    private string _chipText = string.Empty;
    private string _chipGlyph = string.Empty;
    private int _chipLevel;
    private string _chipTooltip = string.Empty;

    public UsageService(SettingsService settings)
    {
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _secrets = new UsageSecretStore(settings.DataDirectory);
        _state = new UsageStateStore(settings.DataDirectory);
    }

    public ObservableCollection<UsageProviderCard> Providers { get; } = new();

    /// <summary>Cards shown in the usage pane: enabled providers in the user's order, then pushed ones.</summary>
    public ObservableCollection<UsageProviderCard> VisibleProviders { get; } = new();

    public bool HasVisibleProviders => VisibleProviders.Count > 0;

    public int RefreshMinutes => Math.Clamp(_settings.Current.UsageRefreshMinutes, 1, 60);

    /// <summary>Metadata used by the dedicated settings window to build one row per provider.</summary>
    public IReadOnlyList<UsageProviderInfo> ProviderInfos
    {
        get
        {
            var ordered = new List<UsageProviderInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in _settings.Current.UsageProviderOrder)
            {
                var provider = _providers.FirstOrDefault(p =>
                    string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                if (provider is not null && seen.Add(provider.Id))
                {
                    ordered.Add(Describe(provider));
                }
            }

            foreach (var provider in _providers.Where(p => seen.Add(p.Id)))
            {
                ordered.Add(Describe(provider));
            }

            return ordered;
        }
    }

    private static UsageProviderInfo Describe(IUsageProvider provider) =>
        new(provider.Id, provider.Name, provider.Glyph, provider.EnvironmentKey,
            provider.Description, provider.HasBalanceSettings,
            provider.ExtraSettingLabel, provider.ExtraSettingHint, provider.KeyPlaceholder);

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? Updated;

    public event Action<UsageAlert>? AlertRequested;

    public bool HasProviders => Providers.Count > 0;

    public string SummaryText
    {
        get
        {
            var count = VisibleProviders.Count;
            if (count == 0)
            {
                return "没有启用的平台，可在设置里开启";
            }

            var needsKey = VisibleProviders.Count(p => p.Status == UsageStatus.NeedsKey);
            return needsKey > 0
                ? $"{count} 个平台 · 每 {RefreshMinutes} 分钟刷新 · {needsKey} 个待配置"
                : $"{count} 个平台 · 每 {RefreshMinutes} 分钟刷新";
        }
    }

    public bool HasChip => _hasChip;

    public string ChipText => _chipText;

    public string ChipGlyph => _chipGlyph;

    public int ChipLevel => _chipLevel;

    public string ChipTooltip => _chipTooltip;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _secrets.Load();
        _state.Load();
        BuildCards();

        _cancellation = new CancellationTokenSource();
        _ = LoopAsync(_cancellation.Token);
    }

    private void BuildCards()
    {
        foreach (var provider in _providers)
        {
            var card = new UsageProviderCard
            {
                ProviderId = provider.Id,
                Name = provider.Name,
                ShortName = provider.ShortName,
                Glyph = provider.Glyph
            };

            var key = ResolveKey(provider);
            card.Status = key is null ? UsageStatus.NeedsKey : UsageStatus.Ok;
            card.StatusText = key is null ? "未配置 API Key" : "等待更新";
            _cards[provider.Id] = card;
            Providers.Add(card);
        }

        RebuildVisible();
        UpdateChip();
    }

    /// <summary>Rebuilds the pane's provider list from enabled providers and the saved order.</summary>
    private void RebuildVisible()
    {
        var enabled = _providers
            .Where(p => p.IsEnabled(_settings.Current))
            .Select(p => p.Id)
            .ToList();

        var order = _settings.Current.UsageProviderOrder;
        var ids = order
            .Where(id => enabled.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Concat(enabled.Where(id => !order.Contains(id, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var desired = new List<UsageProviderCard>();
        foreach (var id in ids)
        {
            var provider = _providers.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (provider is not null && _cards.TryGetValue(provider.Id, out var card))
            {
                desired.Add(card);
            }
        }

        foreach (var card in _cards.Values.Where(c =>
                     c.ProviderId.StartsWith("push:", StringComparison.Ordinal)))
        {
            if (!desired.Contains(card))
            {
                desired.Add(card);
            }
        }

        if (VisibleProviders.SequenceEqual(desired))
        {
            return;
        }

        VisibleProviders.Clear();
        foreach (var card in desired)
        {
            VisibleProviders.Add(card);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasVisibleProviders)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryText)));
        Updated?.Invoke();
    }

    /// <summary>Called after the user rearranges providers; does not refetch.</summary>
    public void ApplyProviderOrder()
    {
        Publish(RebuildVisible);
        Publish(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryText))));
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        await RefreshCoreAsync(force: true, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RefreshCoreAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Manual refresh used by the usage pane and after saving a key.</summary>
    public Task RefreshNowAsync() => RefreshCoreAsync(force: true, _cancellation?.Token ?? CancellationToken.None);

    private async Task RefreshCoreAsync(bool force, CancellationToken cancellationToken)
    {
        if (_disposed || !_settings.Current.UsageEnabled)
        {
            UpdateChip();
            return;
        }

        var now = DateTime.UtcNow;
        var due = _providers.Where(p => p.IsEnabled(_settings.Current))
            .Where(p => force || !_nextFetch.TryGetValue(p.Id, out var at) || now >= at)
            .Select(p => FetchProviderAsync(p, cancellationToken))
            .ToArray();

        // Disabled providers keep their card but stop showing data or a chip.
        foreach (var provider in _providers.Where(p => !p.IsEnabled(_settings.Current)))
        {
            if (_cards.TryGetValue(provider.Id, out var card))
            {
                Publish(() =>
                {
                    card.Status = UsageStatus.NeedsKey;
                    card.StatusText = "已关闭";
                    card.StatusLevel = 0;
                    card.Metrics.Clear();
                    card.OnMetricsChanged();
                    card.HasBalance = false;
                    card.SpendTodayText = string.Empty;
                });
            }
        }

        Publish(RebuildVisible);

        if (due.Length == 0)
        {
            Publish(() =>
            {
                UpdateChip();
                Updated?.Invoke();
            });
            return;
        }

        await Task.WhenAll(due).ConfigureAwait(false);
        Publish(() =>
        {
            UpdateChip();
            Updated?.Invoke();
        });
    }

    private async Task FetchProviderAsync(IUsageProvider provider, CancellationToken cancellationToken)
    {
        if (!_inFlight.TryAdd(provider.Id, 0))
        {
            return;
        }

        try
        {
            var key = ResolveKey(provider);
            if (key is null)
            {
                Publish(() =>
                {
                    if (_cards.TryGetValue(provider.Id, out var card))
                    {
                        card.Status = UsageStatus.NeedsKey;
                        card.StatusText = "未配置 API Key";
                        card.UpdatedText = string.Empty;
                    }
                });
                Schedule(provider, success: false, needsKey: true);
                return;
            }

            UsageFetchResult result;
            try
            {
                var extra = _secrets.Get($"{provider.Id}:extra");
                result = await provider.FetchAsync(key, extra, _settings.Current, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new UsageFetchResult(UsageStatus.Offline, "请求超时", Array.Empty<UsageMetricData>(), null);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpRequestException)
            {
                result = new UsageFetchResult(UsageStatus.Offline, "网络不可用", Array.Empty<UsageMetricData>(), null);
            }
            catch (Exception ex)
            {
                result = new UsageFetchResult(UsageStatus.Error, ex.Message, Array.Empty<UsageMetricData>(), null);
            }

            Publish(() => ApplyResult(provider, result));
            Schedule(provider, result.Status == UsageStatus.Ok, needsKey: result.Status == UsageStatus.NeedsKey);
        }
        finally
        {
            _inFlight.TryRemove(provider.Id, out _);
        }
    }

    private void ApplyResult(IUsageProvider provider, UsageFetchResult result)
    {
        if (!_cards.TryGetValue(provider.Id, out var card))
        {
            return;
        }

        card.Status = result.Status;
        card.ErrorText = result.Error ?? string.Empty;

        switch (result.Status)
        {
            case UsageStatus.Ok:
                card.StatusText = result.Tier is { Length: > 0 } tier
                    ? $"{tier} 套餐"
                    : result.Balance is { Available: false } ? "余额不足" : "正常";
                card.StatusLevel = result.Balance is { Available: false } ? 2 : 0;
                break;
            case UsageStatus.NoPlan:
                card.StatusText = "无生效套餐";
                card.StatusLevel = 1;
                break;
            case UsageStatus.NeedsKey:
                card.StatusText = "未配置 API Key";
                card.StatusLevel = 1;
                break;
            case UsageStatus.Unauthorized:
                card.StatusText = "API Key 无效";
                card.StatusLevel = 2;
                break;
            case UsageStatus.RateLimited:
                card.StatusText = "请求受限";
                card.StatusLevel = 1;
                break;
            case UsageStatus.Offline:
                card.StatusText = "网络不可用";
                card.StatusLevel = 2;
                break;
            default:
                card.StatusText = "更新失败";
                card.StatusLevel = 2;
                break;
        }

        if (result.Status is UsageStatus.Ok or UsageStatus.NoPlan)
        {
            ApplyMetrics(card, result.Metrics);
            ApplyBalance(card, result.Balance);
            card.UpdatedText = $"{DateTime.Now:HH:mm} 更新";
            CheckAlerts(card, result);
        }

        UpdateChip();
        Updated?.Invoke();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryText)));
    }

    private void ApplyMetrics(UsageProviderCard card, IReadOnlyList<UsageMetricData> metrics)
    {
        var same = card.Metrics.Count == metrics.Count;
        if (same)
        {
            for (var i = 0; i < metrics.Count; i++)
            {
                if (!string.Equals(card.Metrics[i].Key, metrics[i].Key, StringComparison.Ordinal) ||
                    !string.Equals(card.Metrics[i].Label, metrics[i].Label, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            card.Metrics.Clear();
            foreach (var metric in metrics)
            {
                card.Metrics.Add(new UsageMetricRow
                {
                    Key = metric.Key,
                    Label = metric.Label,
                    ResetAt = metric.ResetAt
                });
            }
        }

        for (var i = 0; i < metrics.Count; i++)
        {
            var row = card.Metrics[i];
            row.UsedPercent = metrics[i].UsedPercent;
            row.Level = LevelFor(metrics[i].UsedPercent);
            row.ResetText = FormatReset(metrics[i].ResetAt);
        }

        card.OnMetricsChanged();
    }

    private void ApplyBalance(UsageProviderCard card, UsageBalanceData? balance)
    {
        if (balance is null)
        {
            card.HasBalance = false;
            card.BalanceAmount = 0;
            card.SpendTodayText = string.Empty;
            return;
        }

        var symbol = SymbolFor(balance.Currency);
        card.HasBalance = true;
        card.BalanceAmount = balance.Total;
        card.BalanceCurrency = balance.Currency;
        card.BalanceText = $"{symbol}{balance.Total:0.##}";
        card.BalanceDetailText = balance.HasBreakdown
            ? $"赠送 {symbol}{balance.Granted:0.##} · 充值 {symbol}{balance.ToppedUp:0.##}"
            : "钱包余额";

        if (string.Equals(card.ProviderId, "deepseek", StringComparison.Ordinal))
        {
            card.SpendTodayText = UpdateSpend(balance, symbol);
        }

        var budgetMetric = card.Metrics.FirstOrDefault(m => string.Equals(m.Key, "budget", StringComparison.Ordinal));
        card.BudgetPercent = budgetMetric?.UsedPercent;
    }

    private string UpdateSpend(UsageBalanceData balance, string symbol)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.Equals(_state.SpendDate, today, StringComparison.Ordinal))
        {
            _state.SpendDate = today;
            _state.SpendAmount = 0;
            _state.LastBalance = balance.Total;
            _state.LastCurrency = balance.Currency;
            _state.Save();
            return string.Empty;
        }

        if (_state.LastBalance > 0 && balance.Total < _state.LastBalance)
        {
            _state.SpendAmount += _state.LastBalance - balance.Total;
        }

        _state.LastBalance = balance.Total;
        _state.LastCurrency = balance.Currency;
        _state.Save();

        return _state.SpendAmount > 0.004m ? $"今日约 -{symbol}{_state.SpendAmount:0.##}" : string.Empty;
    }

    private void CheckAlerts(UsageProviderCard card, UsageFetchResult result)
    {
        var warn = Math.Clamp(_settings.Current.UsageWarnPercent, 50, 99);
        var critical = Math.Min(100, warn + 15);

        foreach (var metric in card.Metrics)
        {
            var reset = metric.ResetAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "none";
            var suffix = metric.ResetText.Length > 0 ? $" · {metric.ResetText}" : string.Empty;

            if (metric.UsedPercent >= critical)
            {
                MaybeAlert(
                    $"{card.ProviderId}:{metric.Key}:{reset}:c",
                    new UsageAlert(
                        $"{card.ProviderId}:{metric.Key}:c",
                        $"{card.Name} 额度即将用尽",
                        $"{metric.Label}已用 {metric.UsedPercent:0}%{suffix}",
                        "error",
                        7));
            }
            else if (metric.UsedPercent >= warn)
            {
                MaybeAlert(
                    $"{card.ProviderId}:{metric.Key}:{reset}:w",
                    new UsageAlert(
                        $"{card.ProviderId}:{metric.Key}:w",
                        $"{card.Name} 额度提醒",
                        $"{metric.Label}已用 {metric.UsedPercent:0}%{suffix}",
                        "warning",
                        6));
            }
        }

        if (result.Balance is not { } balance)
        {
            return;
        }

        var today = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var symbol = SymbolFor(balance.Currency);

        if (!balance.Available)
        {
            MaybeAlert(
                $"{card.ProviderId}:unavailable:{today}",
                new UsageAlert(
                    $"{card.ProviderId}:unavailable",
                    $"{card.Name} 余额不足",
                    "账户余额不足，API 请求会被拒绝。",
                    "error",
                    8));
        }
        else if (_settings.Current.DeepSeekLowBalance > 0 &&
                 string.Equals(card.ProviderId, "deepseek", StringComparison.Ordinal) &&
                 balance.Total <= _settings.Current.DeepSeekLowBalance)
        {
            MaybeAlert(
                $"{card.ProviderId}:low:{today}",
                new UsageAlert(
                    $"{card.ProviderId}:low",
                    $"{card.Name} 余额偏低",
                    $"当前余额 {symbol}{balance.Total:0.##}，建议及时充值。",
                    "warning",
                    7));
        }
    }

    private void MaybeAlert(string stateKey, UsageAlert alert)
    {
        if (!_state.MarkAlerted(stateKey))
        {
            return;
        }

        Publish(() => AlertRequested?.Invoke(alert));
    }

    private int LevelFor(double used)
    {
        var warn = Math.Clamp(_settings.Current.UsageWarnPercent, 50, 99);
        var critical = Math.Min(100, warn + 15);
        return used >= critical ? 2 : used >= warn ? 1 : 0;
    }

    private void Schedule(IUsageProvider provider, bool success, bool needsKey)
    {
        var interval = TimeSpan.FromMinutes(RefreshMinutes);

        if (success)
        {
            _failures[provider.Id] = 0;
            _nextFetch[provider.Id] = DateTime.UtcNow + interval;
            return;
        }

        if (needsKey)
        {
            _nextFetch[provider.Id] = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            return;
        }

        var failures = _failures.TryGetValue(provider.Id, out var count) ? count + 1 : 1;
        _failures[provider.Id] = failures;
        var backoff = TimeSpan.FromSeconds(Math.Min(
            interval.TotalSeconds * Math.Pow(2, failures),
            TimeSpan.FromMinutes(30).TotalSeconds));
        _nextFetch[provider.Id] = DateTime.UtcNow + backoff;
    }

    private bool IsCardEnabled(string providerId)
    {
        if (providerId.StartsWith("push:", StringComparison.Ordinal))
        {
            return true;
        }

        var provider = _providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
        return provider is null || provider.IsEnabled(_settings.Current);
    }

    private void UpdateChip()
    {        var enabled = _settings.Current.UsageEnabled &&
                      _settings.Current.UsageIslandMode != UsageIslandMode.Off;
        var mode = _settings.Current.UsageIslandMode;
        var warn = Math.Clamp(_settings.Current.UsageWarnPercent, 50, 99);
        var lowBalanceThreshold = _settings.Current.DeepSeekLowBalance;

        UsageProviderCard? bestCard = null;
        UsageMetricRow? bestMetric = null;
        var bestLevel = 0;
        var bestUsed = -1d;

        if (mode == UsageIslandMode.Always)
        {
            // Follow the user's provider order: the first platform that has data
            // is the default chip, so the priority they configured is what shows.
            foreach (var card in VisibleProviders)
            {
                if (!IsCardEnabled(card.ProviderId) || !card.IsConfigured)
                {
                    continue;
                }

                var metric = card.Metrics
                    .OrderByDescending(m => m.UsedPercent)
                    .FirstOrDefault();

                if (metric is null)
                {
                    if (!card.HasBalance)
                    {
                        continue;
                    }

                    bestCard = card;
                    bestLevel = lowBalanceThreshold > 0 && card.BalanceAmount <= lowBalanceThreshold ? 2 : 0;
                    break;
                }

                bestCard = card;
                bestMetric = metric;
                bestLevel = metric.Level;
                break;
            }
        }
        else
        {
            // Alerts-only mode still picks the most severe window so a warning on
            // a lower-priority platform is never hidden.
            void Consider(UsageProviderCard card, UsageMetricRow? metric, int level, double used)
            {
                if (level < 1)
                {
                    return;
                }

                if (level > bestLevel || (level == bestLevel && used > bestUsed))
                {
                    bestCard = card;
                    bestMetric = metric;
                    bestLevel = level;
                    bestUsed = used;
                }
            }

            foreach (var card in _cards.Values)
            {
                if (!IsCardEnabled(card.ProviderId))
                {
                    continue;
                }

                foreach (var metric in card.Metrics)
                {
                    if (metric.UsedPercent < warn)
                    {
                        continue;
                    }

                    Consider(card, metric, metric.Level, metric.UsedPercent);
                }

                if (card.HasBalance && card.Metrics.Count == 0 && card.IsConfigured)
                {
                    var low = lowBalanceThreshold > 0 && card.BalanceAmount <= lowBalanceThreshold;
                    Consider(card, null, low ? 2 : 0, low ? 100 : 0);
                }
            }
        }

        if (!enabled || bestCard is null)
        {
            _hasChip = false;
        }
        else if (bestMetric is not null)
        {
            _hasChip = true;
            _chipText = $"{bestCard.ShortName} {Math.Round(bestMetric.UsedPercent):0}%";
            _chipGlyph = bestCard.Glyph;
            _chipLevel = bestMetric.Level;
        }
        else
        {
            _hasChip = true;
            _chipText = $"{bestCard.ShortName} {bestCard.BalanceText}";
            _chipGlyph = bestCard.BalanceText.StartsWith(bestCard.Glyph, StringComparison.Ordinal)
                ? string.Empty
                : bestCard.Glyph;
            _chipLevel = bestLevel;
        }

        _chipTooltip = BuildTooltip();

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChipText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChipGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChipLevel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChipTooltip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryText)));
    }

    private string BuildTooltip()
    {
        var lines = new List<string>();
        foreach (var card in _cards.Values.Where(c => (c.HasAnyValue || c.IsConfigured) && IsCardEnabled(c.ProviderId)))
        {
            if (card.HasBalance)
            {
                var detail = card.SpendTodayText.Length > 0 ? $" · {card.SpendTodayText}" : string.Empty;
                lines.Add($"{card.Name} · 余额 {card.BalanceText}{detail}");
            }

            foreach (var metric in card.Metrics)
            {
                var reset = metric.ResetText.Length > 0 ? $" · {metric.ResetText}" : string.Empty;
                lines.Add($"{card.Name} · {metric.Label}已用 {metric.PercentText}{reset}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    public void OnSettingsChanged()
    {
        // Apply a new refresh interval immediately instead of waiting for the old due time.
        _nextFetch.Clear();

        Publish(RebuildVisible);
        Publish(() =>
        {
            UpdateChip();
            Updated?.Invoke();
        });

        _ = RefreshNowAsync();
    }

    public string GetKeyStatus(string providerId)
    {
        var provider = _providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
        if (provider is null)
        {
            return "未知服务";
        }

        if (_secrets.Has(providerId))
        {
            return "已保存 · Windows 加密";
        }

        if (provider.EnvironmentKey is { } env &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(env)))
        {
            return $"来自环境变量 {env}";
        }

        if (providerId == "opencode-go" && OpenCodeAuthFile.TryRead() is not null)
        {
            return "来自 opencode 登录状态";
        }

        return "未设置";
    }

    public void SetKey(string providerId, string? key)
    {
        _secrets.Set(providerId, key);
        _failures.TryRemove(providerId, out _);
        _nextFetch.TryRemove(providerId, out _);

        if (_cards.TryGetValue(providerId, out var card))
        {
            if (key is null && ResolveKey(_providers.First(p => p.Id == providerId)) is null)
            {
                card.Status = UsageStatus.NeedsKey;
                card.StatusText = "未配置 API Key";
            }
        }

        UpdateChip();
        _ = RefreshNowAsync();
    }

    /// <summary>Extra per-provider value such as OpenDesign's workspace id.</summary>
    public string? GetExtraSetting(string providerId) => _secrets.Get($"{providerId}:extra");

    public void SetExtraSetting(string providerId, string? value)
    {
        _secrets.Set($"{providerId}:extra", string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        _failures.TryRemove(providerId, out _);
        _nextFetch.TryRemove(providerId, out _);
        _ = RefreshNowAsync();
    }

    private string? ResolveKey(IUsageProvider provider)
    {
        var stored = _secrets.Get(provider.Id);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        if (provider.EnvironmentKey is { } env)
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        if (provider.Id == "opencode-go")
        {
            return OpenCodeAuthFile.TryRead();
        }

        return null;
    }

    /// <summary>Accepts usage pushed by other tools over the island's named pipe.</summary>
    public void ApplyPush(IslandCommand command)
    {
        var providerId = string.IsNullOrWhiteSpace(command.Provider)
            ? "push"
            : $"push:{command.Provider.Trim().ToLowerInvariant()}";

        if (!_cards.TryGetValue(providerId, out var card))
        {
            card = new UsageProviderCard
            {
                ProviderId = providerId,
                Name = string.IsNullOrWhiteSpace(command.ProviderName) ? "外部用量" : command.ProviderName,
                ShortName = string.IsNullOrWhiteSpace(command.Provider) ? "AI" : command.Provider.Trim(),
                Glyph = "•"
            };

            _cards[providerId] = card;
            Providers.Add(card);
        }

        card.Status = UsageStatus.Ok;
        card.StatusText = "正常";
        card.StatusLevel = 0;
        card.UpdatedText = $"{DateTime.Now:HH:mm} 更新";

        var label = string.IsNullOrWhiteSpace(command.Label) ? "用量" : command.Label;
        var percent = command.UsedPercent;

        if (percent is null && command.Limit > 0)
        {
            percent = command.Used / command.Limit * 100d;
        }

        if (percent is { } used)
        {
            var existing = card.Metrics.FirstOrDefault(m => string.Equals(m.Label, label, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new UsageMetricRow { Key = label, Label = label };
                card.Metrics.Add(existing);
            }

            existing.UsedPercent = Math.Clamp(used, 0, 100);
            existing.Level = LevelFor(existing.UsedPercent);
            existing.ResetText = FormatReset(command.ResetAt);

            if (existing.UsedPercent >= Math.Clamp(_settings.Current.UsageWarnPercent, 50, 99))
            {
                MaybeAlert(
                    $"{providerId}:{label}:{command.ResetAt:O}",
                    new UsageAlert(
                        $"{providerId}:{label}",
                        $"{card.Name} 额度提醒",
                        $"{label}已用 {existing.UsedPercent:0}%{(existing.ResetText.Length > 0 ? " · " + existing.ResetText : "")}",
                        existing.Level >= 2 ? "error" : "warning",
                        6));
            }

            card.OnMetricsChanged();
        }

        if (command.Balance > 0)
        {
            var currency = string.IsNullOrWhiteSpace(command.Currency) ? "CNY" : command.Currency;
            card.HasBalance = true;
            card.BalanceText = $"{SymbolFor(currency)}{command.Balance:0.##}";
            card.BalanceDetailText = string.Empty;
        }

        RebuildVisible();
        UpdateChip();
        Updated?.Invoke();
    }

    private static string SymbolFor(string currency) =>
        string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? "$" : "¥";

    private static string FormatReset(DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return string.Empty;
        }

        var span = resetAt.Value - DateTimeOffset.UtcNow;
        if (span <= TimeSpan.Zero)
        {
            return "即将重置";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{Math.Max(1, (int)span.TotalMinutes)} 分钟后重置";
        }

        if (span.TotalHours < 24)
        {
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分后重置";
        }

        return $"{span.Days} 天后重置";
    }

    private void Publish(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _state.Save();
    }
}
