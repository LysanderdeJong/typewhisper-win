using System.Text;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginTranscriptionEngineAdapterTests
{
    [Fact]
    public async Task TranscribeAsync_UsesPcmPathWhenPluginSupportsIt()
    {
        var plugin = new PcmPlugin();
        ITranscriptionEngine engine = new PluginTranscriptionEngineAdapter(plugin);
        float[] samples = [0.25f, -0.5f, 0.0f];

        var result = await engine.TranscribeAsync(samples, "de", TranscriptionTask.Translate, CancellationToken.None);

        Assert.Equal(1, plugin.PcmCallCount);
        Assert.Equal(0, plugin.WavCallCount);
        Assert.Same(samples, plugin.LastSamples);
        Assert.Equal(16000, plugin.LastSampleRate);
        Assert.Equal("de", plugin.LastLanguage);
        Assert.True(plugin.LastTranslate);
        Assert.Equal("pcm", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_FallsBackToWavEncodingWhenPluginNeedsWav()
    {
        var plugin = new WavOnlyPlugin();
        ITranscriptionEngine engine = new PluginTranscriptionEngineAdapter(plugin);

        var result = await engine.TranscribeAsync([1.0f, -1.0f], "en", TranscriptionTask.Transcribe, CancellationToken.None);

        Assert.Equal(1, plugin.WavCallCount);
        Assert.NotNull(plugin.LastWavAudio);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(plugin.LastWavAudio!, 0, 4));
        Assert.Equal("wav", result.Text);
    }

    private sealed class PcmPlugin : ITranscriptionEnginePlugin, IPcmTranscriptionEnginePlugin
    {
        public string PluginId => "com.typewhisper.tests.pcm";
        public string PluginName => "PCM Test Plugin";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "pcm-test";
        public string ProviderDisplayName => "PCM Test";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [];
        public string? SelectedModelId => "model";
        public bool SupportsTranslation => true;
        public int PcmCallCount { get; private set; }
        public int WavCallCount { get; private set; }
        public float[]? LastSamples { get; private set; }
        public int LastSampleRate { get; private set; }
        public string? LastLanguage { get; private set; }
        public bool LastTranslate { get; private set; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void Dispose() { }
        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
        {
            WavCallCount++;
            throw new InvalidOperationException("PCM path should be used instead of WAV fallback.");
        }

        public Task<PluginTranscriptionResult> TranscribePcmAsync(
            float[] audioSamples, int sampleRate, string? language, bool translate, string? prompt, CancellationToken ct)
        {
            PcmCallCount++;
            LastSamples = audioSamples;
            LastSampleRate = sampleRate;
            LastLanguage = language;
            LastTranslate = translate;
            return Task.FromResult(new PluginTranscriptionResult("pcm", language ?? string.Empty, audioSamples.Length / (double)sampleRate));
        }
    }

    private sealed class WavOnlyPlugin : ITranscriptionEnginePlugin
    {
        public string PluginId => "com.typewhisper.tests.wav";
        public string PluginName => "WAV Test Plugin";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "wav-test";
        public string ProviderDisplayName => "WAV Test";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [];
        public string? SelectedModelId => "model";
        public bool SupportsTranslation => false;
        public int WavCallCount { get; private set; }
        public byte[]? LastWavAudio { get; private set; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void Dispose() { }
        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
        {
            WavCallCount++;
            LastWavAudio = wavAudio;
            return Task.FromResult(new PluginTranscriptionResult("wav", language ?? string.Empty, 0));
        }
    }
}
