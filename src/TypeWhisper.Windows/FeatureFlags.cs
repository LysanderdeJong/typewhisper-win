using TypeWhisper.PluginSDK;

namespace TypeWhisper.Windows;

/// <summary>
/// Centralized switches for features that are present in the codebase but not
/// complete enough to expose in the product yet.
/// </summary>
public static class FeatureFlags
{
    public static bool Memory => false;
    public static bool HttpApi => false;
    public static bool WatchFolder => false;

    public static bool IsPluginVisible(string? category, ITypeWhisperPlugin? plugin = null)
    {
        if (plugin is IMemoryStoragePlugin)
            return Memory;

        if (string.Equals(category, "memory", StringComparison.OrdinalIgnoreCase))
            return Memory;

        return true;
    }
}
