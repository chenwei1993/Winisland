using System.Collections.ObjectModel;
using WinIsland.Infrastructure;

namespace WinIsland.Models;

public enum UsageStatus
{
    Ok,
    NeedsKey,
    Unauthorized,
    RateLimited,
    Offline,
    Error
}

/// <summary>One usage / quota window of a provider (5h, weekly, monthly, budget).</summary>
public sealed class UsageMetricRow : ObservableObject
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    private double _usedPercent;

    public double UsedPercent
    {
        get => _usedPercent;
        set
        {
            if (Set(ref _usedPercent, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(PercentText));
            }
        }
    }

    public string PercentText => $"{Math.Round(UsedPercent):0}%";

    /// <summary>0 = normal, 1 = warning, 2 = critical.</summary>
    private int _level;

    public int Level
    {
        get => _level;
        set => Set(ref _level, value);
    }

    private string _resetText = string.Empty;

    public string ResetText
    {
        get => _resetText;
        set
        {
            if (Set(ref _resetText, value))
            {
                OnPropertyChanged(nameof(HasResetText));
            }
        }
    }

    public bool HasResetText => !string.IsNullOrEmpty(ResetText);

    public DateTimeOffset? ResetAt { get; init; }
}

/// <summary>One provider card in the usage pane and its island chip.</summary>
public sealed class UsageProviderCard : ObservableObject
{
    public required string ProviderId { get; init; }

    public required string Name { get; init; }

    /// <summary>Short chip label, e.g. "Go".</summary>
    public required string ShortName { get; init; }

    public required string Glyph { get; init; }

    public ObservableCollection<UsageMetricRow> Metrics { get; } = new();

    public bool HasMetrics => Metrics.Count > 0;

    public void OnMetricsChanged()
    {
        OnPropertyChanged(nameof(HasMetrics));
    }

    private UsageStatus _status = UsageStatus.NeedsKey;

    public UsageStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(IsConfigured));
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool IsConfigured => Status != UsageStatus.NeedsKey;

    private string _statusText = "未配置";

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private int _statusLevel;

    public int StatusLevel
    {
        get => _statusLevel;
        set => Set(ref _statusLevel, value);
    }

    private string _errorText = string.Empty;

    public string ErrorText
    {
        get => _errorText;
        set
        {
            if (Set(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>Only shown when the provider has a problem beyond "needs key".</summary>
    public bool HasError => Status is UsageStatus.Unauthorized or UsageStatus.RateLimited
        or UsageStatus.Offline or UsageStatus.Error;

    // ── Balance (DeepSeek and other pay-as-you-go providers) ────────────

    private bool _hasBalance;

    public bool HasBalance
    {
        get => _hasBalance;
        set => Set(ref _hasBalance, value);
    }

    private string _balanceText = string.Empty;

    public string BalanceText
    {
        get => _balanceText;
        set => Set(ref _balanceText, value);
    }

    public decimal BalanceAmount { get; set; }

    public string BalanceCurrency { get; set; } = "CNY";

    private string _balanceDetailText = string.Empty;

    public string BalanceDetailText
    {
        get => _balanceDetailText;
        set => Set(ref _balanceDetailText, value);
    }

    private string _spendTodayText = string.Empty;

    public string SpendTodayText
    {
        get => _spendTodayText;
        set
        {
            if (Set(ref _spendTodayText, value))
            {
                OnPropertyChanged(nameof(HasSpendToday));
            }
        }
    }

    public bool HasSpendToday => !string.IsNullOrEmpty(SpendTodayText);

    private double? _budgetPercent;

    public double? BudgetPercent
    {
        get => _budgetPercent;
        set
        {
            if (Set(ref _budgetPercent, value))
            {
                OnPropertyChanged(nameof(HasBudget));
            }
        }
    }

    public bool HasBudget => BudgetPercent is not null;

    // ── Freshness ───────────────────────────────────────────────────────

    private string _updatedText = string.Empty;

    public string UpdatedText
    {
        get => _updatedText;
        set => Set(ref _updatedText, value);
    }

    public bool HasAnyValue => HasBalance || HasMetrics;
}

public sealed record UsageAlert(string Key, string Title, string Message, string Kind, double Seconds);

/// <summary>Row of the dedicated AI usage settings window.</summary>
public sealed class UsageProviderSettingRow : ObservableObject
{
    public UsageProviderSettingRow(bool enabled)
    {
        _isEnabled = enabled;
    }

    public required string ProviderId { get; init; }

    public required string Name { get; init; }

    public required string Glyph { get; init; }

    public string? EnvironmentKey { get; init; }

    public string Description { get; init; } = string.Empty;

    public bool HasBalanceSettings { get; init; }

    public bool HasEnvironmentKey => !string.IsNullOrEmpty(EnvironmentKey);

    public string EnvironmentHint => HasEnvironmentKey ? $"环境变量：{EnvironmentKey}" : string.Empty;

    public Action<bool>? EnabledChanged { get; init; }

    public Action<decimal>? BudgetChanged { get; init; }

    public Action<decimal>? LowBalanceChanged { get; init; }

    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
            {
                EnabledChanged?.Invoke(value);
            }
        }
    }

    private decimal _budget;

    public decimal Budget
    {
        get => _budget;
        set
        {
            if (Set(ref _budget, Math.Max(0, value)))
            {
                BudgetChanged?.Invoke(_budget);
            }
        }
    }

    private decimal _lowBalance;

    public decimal LowBalance
    {
        get => _lowBalance;
        set
        {
            if (Set(ref _lowBalance, Math.Max(0, value)))
            {
                LowBalanceChanged?.Invoke(_lowBalance);
            }
        }
    }

    private string _keyInput = string.Empty;

    /// <summary>What the user typed; never pre-filled with the stored key.</summary>
    public string KeyInput
    {
        get => _keyInput;
        set => Set(ref _keyInput, value);
    }

    private string _keyStatus = "未设置";

    public string KeyStatus
    {
        get => _keyStatus;
        set => Set(ref _keyStatus, value);
    }
}
