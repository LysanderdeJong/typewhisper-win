using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional transcription plugin interface for engines that can consume raw PCM samples directly.
/// This lets local engines avoid a WAV encode/decode roundtrip at the host boundary.
/// </summary>
public interface IPcmTranscriptionEnginePlugin
{
    /// <summary>
    /// Transcribes mono PCM float samples at the provided sample rate.
    /// </summary>
    Task<PluginTranscriptionResult> TranscribePcmAsync(
        float[] audioSamples,
        int sampleRate,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct);
}
