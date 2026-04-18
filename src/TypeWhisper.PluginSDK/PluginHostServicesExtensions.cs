namespace TypeWhisper.PluginSDK;

public static class PluginHostServicesExtensions
{
    public static Task StoreOrDeleteSecretAsync(this IPluginHostServices? host, string key, string? value) =>
        host is null
            ? Task.CompletedTask
            : string.IsNullOrWhiteSpace(value)
                ? host.DeleteSecretAsync(key)
                : host.StoreSecretAsync(key, value);
}
