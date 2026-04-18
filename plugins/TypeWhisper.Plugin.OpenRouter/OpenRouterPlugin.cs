using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenRouter;

public sealed class OpenRouterPlugin : ILlmProviderPlugin
{
    private const string BaseUrl = "https://openrouter.ai/api";

    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;
    private string? _apiKey;

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.openrouter";
    public string PluginName => "OpenRouter";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new OpenRouterSettingsView(this);

    // ILlmProviderPlugin

    public string ProviderName => "OpenRouter";
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
    [
        new PluginModelInfo("anthropic/claude-sonnet-4", "Claude Sonnet 4") { IsRecommended = true },
        new PluginModelInfo("google/gemini-2.5-flash", "Gemini 2.5 Flash"),
        new PluginModelInfo("meta-llama/llama-4-scout", "Llama 4 Scout"),
    ];

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("API key not configured");

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient, BaseUrl, _apiKey!, model, systemPrompt, userText, ct);
    }

    // API key management (for settings view)

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;

    internal Task SetApiKeyAsync(string apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        return _host.StoreOrDeleteSecretAsync("api-key", _apiKey);
    }

    internal Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default) =>
        OpenAiApiHelper.ValidateApiKeyAsync(_httpClient, $"{BaseUrl}/v1/models", apiKey, ct: ct);

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
