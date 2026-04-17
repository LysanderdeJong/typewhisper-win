using System.Windows.Controls;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.Groq;

public partial class GroqSettingsView : UserControl
{
    private readonly GroqPlugin _plugin;

    public GroqSettingsView(GroqPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        ApiKeySettingsViewHelper.Initialize(TestButton, ApiKeyBox, plugin.ApiKey, plugin.Loc);
    }

    private async void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e) =>
        await ApiKeySettingsViewHelper.SaveAsync(ApiKeyBox, StatusText, _plugin.SetApiKeyAsync, _plugin.Loc);

    private async void OnTestClick(object sender, System.Windows.RoutedEventArgs e) =>
        await ApiKeySettingsViewHelper.TestAsync(ApiKeyBox, TestButton, StatusText, _plugin.ValidateApiKeyAsync, _plugin.Loc);
}
