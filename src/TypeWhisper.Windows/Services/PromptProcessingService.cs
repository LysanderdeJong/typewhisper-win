using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.Services;

public sealed class PromptProcessingService
{
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;

    public PromptProcessingService(PluginManager pluginManager, ISettingsService settings)
    {
        _pluginManager = pluginManager;
        _settings = settings;
    }

    public bool IsAnyProviderAvailable =>
        _pluginManager.LlmProviders.Any(p => p.IsAvailable);

    public async Task<string> ProcessAsync(PromptAction action, string inputText, CancellationToken ct)
    {
        var (provider, modelId) = ResolveProvider(action);
        if (provider is null)
            throw new InvalidOperationException(Loc.Instance["Error.NoLlmProvider"]);

        Debug.WriteLine($"[PromptProcessing] Using provider '{provider.ProviderName}' model '{modelId}' for action '{action.Name}'");

        return await provider.ProcessAsync(action.SystemPrompt, inputText, modelId, ct);
    }

    private (ILlmProviderPlugin? Provider, string ModelId) ResolveProvider(PromptAction action)
    {
        // 1. Per-prompt override
        if (!string.IsNullOrEmpty(action.ProviderOverride))
        {
            var result = ResolvePluginModelId(action.ProviderOverride);
            if (result.Provider is not null) return result;
        }

        // 2. Default LLM provider from settings
        var defaultProvider = _settings.Current.DefaultLlmProvider;
        if (!string.IsNullOrEmpty(defaultProvider))
        {
            var result = ResolvePluginModelId(defaultProvider);
            if (result.Provider is not null) return result;
        }

        // 3. First available provider
        foreach (var provider in _pluginManager.LlmProviders)
        {
            if (!provider.IsAvailable) continue;
            var firstModel = provider.SupportedModels.FirstOrDefault();
            if (firstModel is not null)
                return (provider, firstModel.Id);
        }

        return (null, "");
    }

    private (ILlmProviderPlugin? Provider, string ModelId) ResolvePluginModelId(string pluginModelId)
    {
        // Format: plugin:{pluginId}:{modelId}
        var parts = pluginModelId.Split(':', 3);
        if (parts.Length < 3 || parts[0] != "plugin")
            return (null, "");

        var pluginId = parts[1];
        var modelId = parts[2];

        var provider = _pluginManager.LlmProviders
            .FirstOrDefault(p => p is ITypeWhisperPlugin twp &&
                _pluginManager.GetPlugin(pluginId)?.Instance == twp &&
                p.IsAvailable);

        return provider is not null ? (provider, modelId) : (null, "");
    }
}
