using System.IO;
using System.Text.Json;

namespace WinIsland.Services.Usage;

/// <summary>
/// Best-effort read of the key OpenCode already stored when the user signed in
/// to OpenCode Go. Only entries whose provider name mentions OpenCode or Go are
/// considered, so keys for other providers are never picked up.
/// </summary>
internal static class OpenCodeAuthFile
{
    private static string[] CandidatePaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(home, ".local", "share", "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "opencode", "auth.json")
        ];
    }

    public static string? TryRead()
    {
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (FindKey(document.RootElement, string.Empty) is { } key)
                {
                    return key;
                }
            }
            catch
            {
                // Ignore unreadable or foreign auth files.
            }
        }

        return null;
    }

    private static string? FindKey(JsonElement element, string providerName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                if (FindKey(property.Value, property.Name) is { } nested)
                {
                    return nested;
                }

                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String ||
                !IsGoProvider(providerName) ||
                !IsKeyName(property.Name))
            {
                continue;
            }

            var value = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value.StartsWith("sk-", StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsKeyName(string name) =>
        name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("apiKey", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("access", StringComparison.OrdinalIgnoreCase);

    private static bool IsGoProvider(string name) =>
        name.Contains("opencode", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("zen", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("go", StringComparison.OrdinalIgnoreCase);
}
