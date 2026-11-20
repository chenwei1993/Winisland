namespace WinIsland.Services.Usage;

/// <summary>Per-provider enable flags stored by id, so adding a provider needs no schema change.</summary>
public static class UsageProviderSettings
{
    public static bool IsEnabled(AppSettings settings, string providerId)
    {
        return !settings.UsageProviders.TryGetValue(providerId, out var enabled) || enabled;
    }

    public static void SetEnabled(AppSettings settings, string providerId, bool enabled)
    {
        settings.UsageProviders[providerId] = enabled;
    }
}
