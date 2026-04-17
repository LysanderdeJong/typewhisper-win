using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
/// Static helper for Whisper-compatible audio transcription API calls.
/// Extracted from CloudProviderBase for reuse by transcription engine plugins.
/// </summary>
public static class OpenAiTranscriptionHelper
{
    /// <summary>
    /// Sends a transcription request to a Whisper-compatible API endpoint.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="baseUrl">API base URL (e.g. "https://api.openai.com").</param>
    /// <param name="apiKey">Bearer token for authentication.</param>
    /// <param name="model">Model identifier (e.g. "whisper-1").</param>
    /// <param name="wavAudio">WAV-encoded audio bytes.</param>
    /// <param name="language">Language hint (ISO code) or null for auto-detection.</param>
    /// <param name="translate">If true, uses the translations endpoint (audio to English).</param>
    /// <param name="responseFormat">Response format (e.g. "verbose_json", "json", "text").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transcription result with text, detected language, and duration.</returns>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Each HttpContent is handed off to MultipartFormDataContent via AddDisposable, "
            + "which disposes the child on a failed Add and otherwise transfers ownership to the "
            + "parent MultipartFormDataContent whose Dispose() disposes all children.")]
    public static async Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient, string baseUrl, string apiKey,
        string model, byte[] wavAudio, string? language, bool translate,
        string responseFormat, CancellationToken ct)
    {
        var endpoint = translate
            ? $"{baseUrl}/v1/audio/translations"
            : $"{baseUrl}/v1/audio/transcriptions";

        // MultipartFormDataContent.Dispose() disposes its child contents, so children added
        // successfully are covered by the outer using. Children created but not yet added
        // (e.g. if Add() throws) are wrapped individually to avoid leaks on the failure path.
        using var content = new MultipartFormDataContent();

        AddDisposable(content, CreateAudioContent(wavAudio), "file", "audio.wav");
        AddDisposable(content, new StringContent(model), "model");
        AddDisposable(content, new StringContent(responseFormat), "response_format");

        if (!string.IsNullOrEmpty(language) && language != "auto")
            AddDisposable(content, new StringContent(language), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = content;

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json);
    }

    /// <summary>
    /// Creates the audio ByteArrayContent with the proper content-type.
    /// Factored out so the create/add pair can be protected against Add() failures.
    /// </summary>
    private static ByteArrayContent CreateAudioContent(byte[] wavAudio)
    {
        var fileContent = new ByteArrayContent(wavAudio);
        try
        {
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            return fileContent;
        }
        catch
        {
            fileContent.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Adds a child HttpContent to a MultipartFormDataContent, disposing the child if Add throws.
    /// Once successfully added, the parent's Dispose() is responsible for disposing the child.
    /// </summary>
    private static void AddDisposable(MultipartFormDataContent parent, HttpContent child, string name)
    {
        try
        {
            parent.Add(child, name);
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Adds a child HttpContent to a MultipartFormDataContent with a file name, disposing the
    /// child if Add throws. Once successfully added, the parent's Dispose() owns the child.
    /// </summary>
    private static void AddDisposable(MultipartFormDataContent parent, HttpContent child, string name, string fileName)
    {
        try
        {
            parent.Add(child, name, fileName);
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Parses a Whisper-compatible JSON transcription response.
    /// </summary>
    internal static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
        var language = root.TryGetProperty("language", out var langEl) ? langEl.GetString() : null;
        var duration = root.TryGetProperty("duration", out var durEl) ? durEl.GetDouble() : 0;

        // Extract min no_speech_prob from segments (verbose_json format).
        // Using min so that the filter only triggers when ALL segments are silence.
        float? minNoSpeechProb = null;
        if (root.TryGetProperty("segments", out var segmentsEl)
            && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                if (seg.TryGetProperty("no_speech_prob", out var nspEl))
                {
                    var prob = (float)nspEl.GetDouble();
                    minNoSpeechProb = minNoSpeechProb is null
                        ? prob
                        : Math.Min(minNoSpeechProb.Value, prob);
                }
            }
        }

        return new PluginTranscriptionResult(text.Trim(), language, duration, minNoSpeechProb);
    }
}
