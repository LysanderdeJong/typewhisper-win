using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides live transcription during recording. Uses real-time WebSocket streaming
/// when the plugin supports it, otherwise falls back to polling-based transcription.
/// </summary>
public sealed class StreamingHandler : IDisposable
{
    private readonly ModelManagerService _modelManager;
    private readonly AudioRecordingService _audio;
    private readonly IDictionaryService _dictionary;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly StreamingTranscriptState _transcriptState = new();

    private CancellationTokenSource? _cts;
    private Task? _streamingTask;
    private IStreamingSession? _session;
    private Action<StreamingTranscriptEvent>? _transcriptHandler;

    public Action<string>? OnPartialTextUpdate { get; set; }

    public StreamingHandler(
        ModelManagerService modelManager,
        AudioRecordingService audio,
        IDictionaryService dictionary)
    {
        _modelManager = modelManager;
        _audio = audio;
        _dictionary = dictionary;
    }

    public void Start(
        string? language,
        TranscriptionTask task,
        Func<bool> isStillRecording)
    {
        Stop();

        var sessionVersion = _transcriptState.StartSession();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var plugin = _modelManager.ActiveTranscriptionPlugin;

        if (plugin is not null && plugin.SupportsStreaming)
            _streamingTask = RunWebSocketStreamingAsync(plugin, language, sessionVersion, ct);
        else
            _streamingTask = RunPollingFallbackAsync(language, task, isStillRecording, sessionVersion, ct);
    }

    public string Stop()
    {
        _audio.SamplesAvailable -= OnStreamingSamplesAvailable;
        _cts?.Cancel();

        var finalText = _transcriptState.StopSession();

        var session = _session;
        var transcriptHandler = _transcriptHandler;
        _session = null;
        _transcriptHandler = null;

        if (session is not null && transcriptHandler is not null)
            session.TranscriptReceived -= transcriptHandler;

        if (session is not null)
        {
            // Fire-and-forget with timeout to avoid deadlock
            _ = CleanupSessionAsync(session);
        }

        _cts?.Dispose();
        _cts = null;
        _streamingTask = null;

        return finalText;
    }

    private static async Task CleanupSessionAsync(IStreamingSession session)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await session.FinalizeAsync(timeoutCts.Token); }
        catch { /* best effort */ }
        try { await session.DisposeAsync(); }
        catch { /* best effort */ }
    }

    // ── WebSocket streaming path ──

    private async Task RunWebSocketStreamingAsync(
        ITranscriptionEnginePlugin plugin, string? language, int sessionVersion, CancellationToken ct)
    {
        try
        {
            var lang = language == "auto" ? null : language;
            var session = await plugin.StartStreamingAsync(lang, ct);
            if (!_transcriptState.IsCurrentSession(sessionVersion) || ct.IsCancellationRequested)
            {
                await CleanupSessionAsync(session);
                return;
            }

            _session = session;
            _transcriptHandler = evt => OnTranscriptReceived(evt, sessionVersion);
            session.TranscriptReceived += _transcriptHandler;
            _audio.SamplesAvailable += OnStreamingSamplesAvailable;

            // Keep alive until cancelled
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebSocket streaming error: {ex.Message}");
        }
    }

    private void OnStreamingSamplesAvailable(object? sender, SamplesAvailableEventArgs e)
    {
        var session = _session;
        var cts = _cts;
        if (session is null || cts is null || cts.IsCancellationRequested) return;

        var pcm16 = RentPcm16(e.Samples, out var byteCount);
        _ = SendAudioChunkAsync(session, pcm16, byteCount, cts.Token);
    }

    private async Task SendAudioChunkAsync(IStreamingSession session, byte[] pcm16, int byteCount, CancellationToken ct)
    {
        try
        {
            await _sendGate.WaitAsync(ct);
            try
            {
                if (!ct.IsCancellationRequested)
                    await session.SendAudioAsync(pcm16.AsMemory(0, byteCount), ct);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException or WebSocketException)
        {
            Debug.WriteLine($"SendAudio error: {ex.Message}");
        }
        finally { ArrayPool<byte>.Shared.Return(pcm16); }
    }

    private void OnTranscriptReceived(StreamingTranscriptEvent evt, int sessionVersion)
    {
        if (_cts is null || _cts.IsCancellationRequested)
            return;

        if (_transcriptState.TryApplyRealtime(sessionVersion, evt, _dictionary.ApplyCorrections, out var display))
            OnPartialTextUpdate?.Invoke(display);
    }

    // ── Polling fallback path ──

    private async Task RunPollingFallbackAsync(
        string? language, TranscriptionTask task,
        Func<bool> isStillRecording, int sessionVersion, CancellationToken ct)
    {
        var engine = _modelManager.Engine;
        var pollInterval = TimeSpan.FromSeconds(3.0);

        try
        {
            await Task.Delay(pollInterval, ct);

            while (!ct.IsCancellationRequested && isStillRecording())
            {
                var buffer = _audio.GetCurrentBuffer();
                var bufferDuration = buffer is not null ? buffer.Length / 16000.0 : 0;

                if (buffer is not null && bufferDuration > 0.5
                    && _audio.PeakRmsLevel >= AudioRecordingService.SpeechEnergyThreshold)
                {
                    try
                    {
                        var lang = language == "auto" ? null : language;
                        var result = await engine.TranscribeAsync(buffer, lang, task, ct);

                        if (result.NoSpeechProbability is > 0.8f)
                            continue;

                        var text = result.Text?.Trim() ?? "";

                        if (!string.IsNullOrEmpty(text))
                        {
                            if (_transcriptState.TryApplyPolling(
                                sessionVersion,
                                text,
                                _dictionary.ApplyCorrections,
                                out var stable))
                            {
                                OnPartialTextUpdate?.Invoke(stable);
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Streaming transcription error (non-fatal): {ex.Message}");
                    }
                }

                await Task.Delay(pollInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Helpers ──

    private static byte[] RentPcm16(float[] samples, out int byteCount)
    {
        byteCount = samples.Length * 2;
        var bytes = ArrayPool<byte>.Shared.Rent(byteCount);
        WritePcm16(bytes.AsSpan(0, byteCount), samples);
        return bytes;
    }

    private static void WritePcm16(Span<byte> destination, ReadOnlySpan<float> samples)
        => PcmSampleConverter.ConvertFloatToPcm16Le(samples, destination);

    /// <summary>
    /// Keeps confirmed text stable and only appends new content.
    /// Used only in polling fallback path.
    /// </summary>
    public static string StabilizeText(string confirmed, string newText)
    {
        newText = newText.Trim();
        if (string.IsNullOrEmpty(confirmed)) return newText;
        if (string.IsNullOrEmpty(newText)) return confirmed;

        if (newText.StartsWith(confirmed, StringComparison.Ordinal))
            return newText;

        var matchEnd = 0;
        var minLen = Math.Min(confirmed.Length, newText.Length);
        for (var i = 0; i < minLen; i++)
        {
            if (confirmed[i] == newText[i])
                matchEnd = i + 1;
            else
                break;
        }

        if (matchEnd > confirmed.Length / 2)
        {
            var tail = newText[matchEnd..];
            if (tail.Length > 0 && !confirmed.EndsWith(' ') && !tail.StartsWith(' '))
                return confirmed + " " + tail;
            return confirmed + tail;
        }

        var minOverlap = Math.Min(20, confirmed.Length / 4);
        var maxShift = Math.Min(confirmed.Length - minOverlap, 150);
        if (maxShift > 0)
        {
            for (var dropCount = 1; dropCount <= maxShift; dropCount++)
            {
                var suffix = confirmed[dropCount..];
                if (newText.StartsWith(suffix, StringComparison.Ordinal))
                {
                    var newTail = newText[(confirmed.Length - dropCount)..];
                    return string.IsNullOrEmpty(newTail) ? confirmed : confirmed + newTail;
                }
            }
        }

        return newText;
    }

    public void Dispose()
    {
        Stop();
        _sendGate.Dispose();
    }
}
