using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.PluginSDK.Helpers;

public static class ApiKeySettingsViewHelper
{
    public static void Initialize(Button testButton, PasswordBox apiKeyBox, string? apiKey, IPluginLocalization? loc)
    {
        testButton.Content = L(loc, "Settings.Test");
        if (!string.IsNullOrEmpty(apiKey))
            apiKeyBox.Password = apiKey;
    }

    public static async Task SaveAsync(PasswordBox apiKeyBox, TextBlock statusText, Func<string, Task> setApiKeyAsync, IPluginLocalization? loc)
    {
        var key = apiKeyBox.Password;
        await setApiKeyAsync(key);
        statusText.Text = string.IsNullOrWhiteSpace(key) ? "" : L(loc, "Settings.Saved");
        statusText.Foreground = Brushes.Gray;
    }

    public static async Task TestAsync(
        PasswordBox apiKeyBox,
        Button testButton,
        TextBlock statusText,
        Func<string, CancellationToken, Task<bool>> validateApiKeyAsync,
        IPluginLocalization? loc)
    {
        var key = apiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            statusText.Text = L(loc, "Settings.EnterApiKey");
            statusText.Foreground = Brushes.Orange;
            return;
        }

        testButton.IsEnabled = false;
        statusText.Text = L(loc, "Settings.Testing");
        statusText.Foreground = Brushes.Gray;

        try
        {
            var valid = await validateApiKeyAsync(key, CancellationToken.None);
            statusText.Text = L(loc, valid ? "Settings.ApiKeyValid" : "Settings.ApiKeyInvalid");
            statusText.Foreground = valid ? Brushes.Green : Brushes.Red;
        }
        catch (Exception ex)
        {
            statusText.Text = L(loc, "Settings.Error", ex.Message);
            statusText.Foreground = Brushes.Red;
        }
        finally
        {
            testButton.IsEnabled = true;
        }
    }

    private static string L(IPluginLocalization? loc, string key) => loc?.GetString(key) ?? key;
    private static string L(IPluginLocalization? loc, string key, params object[] args) => loc?.GetString(key, args) ?? key;
}
