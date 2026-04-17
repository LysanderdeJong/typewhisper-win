using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows;
using TypeWhisper.Windows.ViewModels;
using System.Diagnostics;

namespace TypeWhisper.Windows.Services;

public sealed class HttpApiService : IDisposable
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;
    private readonly IHistoryService _history;
    private readonly IProfileService _profiles;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly DictationViewModel _dictation;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    public bool IsRunning => _listener?.IsListening == true;

    private static (int, string) Json(int code, object body) => (code, JsonSerializer.Serialize(body));

    public HttpApiService(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        IHistoryService history,
        IProfileService profiles,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline,
        DictationViewModel dictation)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
        _history = history;
        _profiles = profiles;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;
        _dictation = dictation;
    }

    public void Start(int port)
    {
        if (!FeatureFlags.HttpApi)
            return;

        if (_listener is { IsListening: true }) return;

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();

        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context, ct), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* continue listening */ }
        }
    }

    private async Task HandleRequest(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var path = request.Url?.AbsolutePath ?? "";
            var (statusCode, body) = (path, request.HttpMethod) switch
            {
                ("/v1/status", "GET") => HandleStatus(),
                ("/v1/models", "GET") => HandleModels(),
                ("/v1/transcribe", "POST") => await HandleTranscribe(request, ct),
                ("/v1/history", "GET") => HandleHistorySearch(request),
                ("/v1/history", "DELETE") => HandleHistoryDelete(request),
                ("/v1/profiles", "GET") => HandleProfilesList(),
                ("/v1/profiles/toggle", "PUT") => HandleProfileToggle(request),
                ("/v1/dictation/start", "POST") => await HandleDictationStart(),
                ("/v1/dictation/stop", "POST") => await HandleDictationStop(),
                ("/v1/dictation/status", "GET") => HandleDictationStatus(),
                _ => Json(404, new { error = "Not found" })
            };
            await WriteJsonAsync(response, statusCode, body, ct);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(response, 500, JsonSerializer.Serialize(new { error = ex.Message }), ct);
        }
        finally { response.Close(); }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, string body, CancellationToken ct)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct);
    }

    private (int, string) HandleStatus()
    {
        var activePlugin = _modelManager.ActiveTranscriptionPlugin;
        return Json(200, new
        {
            status = _modelManager.Engine.IsModelLoaded ? "ready" : "no_model",
            activeModel = _modelManager.ActiveModelId,
            apiVersion = "1.0",
            supports_streaming = activePlugin?.SupportsStreaming ?? false,
            supports_translation = activePlugin?.SupportsTranslation ?? false
        });
    }

    private (int, string) HandleModels()
    {
        var models = _modelManager.PluginManager.TranscriptionEngines
            .SelectMany(e => e.TranscriptionModels.Select(m =>
            {
                var fullId = ModelManagerService.GetPluginModelId(e.PluginId, m.Id);
                return new
                {
                    id = fullId,
                    name = $"{e.ProviderDisplayName}: {m.DisplayName}",
                    size = m.SizeDescription ?? (e.SupportsModelDownload ? "Local" : "Cloud"),
                    engine = e.PluginId,
                    downloaded = _modelManager.IsDownloaded(fullId),
                    active = _modelManager.ActiveModelId == fullId
                };
            }));
        return Json(200, new { models });
    }

    private async Task<(int, string)> HandleTranscribe(HttpListenerRequest request, CancellationToken ct)
    {
        if (!_modelManager.Engine.IsModelLoaded)
            return Json(503, new { error = "No model loaded" });

        var tempPath = Path.Combine(Path.GetTempPath(), $"tw_api_{Guid.NewGuid()}.tmp");
        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await request.InputStream.CopyToAsync(fs, ct);
            }

            var s = _settings.Current;
            var language = request.QueryString["language"] ?? (s.Language == "auto" ? null : s.Language);
            var taskStr = request.QueryString["task"] ?? s.TranscriptionTask;
            var task = taskStr == "translate" ? TranscriptionTask.Translate : TranscriptionTask.Transcribe;
            var responseFormat = request.QueryString["response_format"] ?? "json";

            var pipelineOptions = new PipelineOptions
            {
                VocabularyBooster = GetVocabularyBooster(),
                DictionaryCorrector = _dictionary.ApplyCorrections
            };

            var totalDuration = AudioFileService.GetDuration(tempPath).TotalSeconds;
            var result = FileTranscriptionMemoryPolicy.ShouldUseFileBackedTranscription(totalDuration, useSpeakerDiarization: false)
                ? await TranscribeFileInChunksAsync(tempPath, totalDuration, language, task, pipelineOptions, ct)
                : await TranscribeWholeFileAsync(await _audioFile.LoadAudioAsync(tempPath, ct), language, task, pipelineOptions, ct);

            return Json(200, new
            {
                text = result.Text,
                language = result.DetectedLanguage,
                duration = result.Duration,
                processing_time = result.ProcessingTime,
                segments = result.Segments.Select(seg => new { text = seg.Text, start = seg.Start, end = seg.End })
            });
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private async Task<TranscriptionResult> TranscribeWholeFileAsync(
        float[] samples,
        string? language,
        TranscriptionTask task,
        PipelineOptions pipelineOptions,
        CancellationToken ct)
    {
        var result = await _modelManager.Engine.TranscribeAsync(samples, language, task, ct);
        var pipelineResult = await _pipeline.ProcessAsync(result.Text, pipelineOptions, ct);

        return result with
        {
            Text = pipelineResult.Text
        };
    }

    private async Task<TranscriptionResult> TranscribeFileInChunksAsync(
        string filePath,
        double totalDuration,
        string? language,
        TranscriptionTask task,
        PipelineOptions options,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? detectedLanguage = null;
        var processedSegments = new List<TranscriptionSegment>();

        await foreach (var chunk in _audioFile.StreamAudioChunksAsync(
            filePath,
            FileTranscriptionMemoryPolicy.FileBackedTranscriptionChunkSamples,
            ct))
        {
            ct.ThrowIfCancellationRequested();

            var rawResult = await _modelManager.Engine.TranscribeAsync(chunk.Samples, language, task, ct);
            detectedLanguage ??= rawResult.DetectedLanguage;

            if (rawResult.Segments.Count > 0)
            {
                foreach (var rawSegment in rawResult.Segments)
                {
                    var processed = await _pipeline.ProcessAsync(rawSegment.Text, options, ct);
                    var text = processed.Text.Trim();
                    if (text.Length == 0)
                        continue;

                    processedSegments.Add(new TranscriptionSegment(
                        text,
                        chunk.StartSeconds + rawSegment.Start,
                        chunk.StartSeconds + rawSegment.End));
                }

                continue;
            }

            var pipelineResult = await _pipeline.ProcessAsync(rawResult.Text, options, ct);
            var chunkText = pipelineResult.Text.Trim();
            if (chunkText.Length == 0)
                continue;

            processedSegments.Add(new TranscriptionSegment(chunkText, chunk.StartSeconds, chunk.EndSeconds));
        }

        stopwatch.Stop();

        return new TranscriptionResult
        {
            Text = string.Join(" ", processedSegments.Select(segment => segment.Text)),
            DetectedLanguage = detectedLanguage,
            Duration = totalDuration,
            ProcessingTime = stopwatch.Elapsed.TotalSeconds,
            Segments = processedSegments,
        };
    }

    // GET /v1/history?q=&limit=&offset=
    private (int, string) HandleHistorySearch(HttpListenerRequest request)
    {
        var query = request.QueryString["q"] ?? "";
        var limitStr = request.QueryString["limit"];
        var offsetStr = request.QueryString["offset"];

        var limit = int.TryParse(limitStr, out var l) ? l : 50;
        var offset = int.TryParse(offsetStr, out var o) ? o : 0;

        var records = string.IsNullOrWhiteSpace(query)
            ? _history.Records
            : _history.Search(query);

        var paged = records.Skip(offset).Take(limit).Select(r => new
        {
            id = r.Id,
            timestamp = r.Timestamp.ToString("o"),
            text = r.FinalText,
            raw_text = r.RawText,
            app = r.AppProcessName,
            duration = r.DurationSeconds,
            language = r.Language,
            engine = r.EngineUsed,
            model = r.ModelUsed,
            profile = r.ProfileName,
            words = r.WordCount
        });

        return Json(200, new { total = records.Count, offset, limit, records = paged });
    }

    // DELETE /v1/history?id=
    private (int, string) HandleHistoryDelete(HttpListenerRequest request)
    {
        var id = request.QueryString["id"];
        if (string.IsNullOrEmpty(id))
            return Json(400, new { error = "Missing id parameter" });

        _history.DeleteRecord(id);
        return Json(200, new { deleted = true, id });
    }

    // GET /v1/profiles
    private (int, string) HandleProfilesList()
    {
        var profiles = _profiles.Profiles.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            is_enabled = p.IsEnabled,
            priority = p.Priority,
            process_names = p.ProcessNames,
            url_patterns = p.UrlPatterns,
            input_language = p.InputLanguage,
            translation_target = p.TranslationTarget,
            selected_task = p.SelectedTask,
            model_override = p.TranscriptionModelOverride,
            prompt_action_id = p.PromptActionId
        });

        return Json(200, new { profiles });
    }

    // PUT /v1/profiles/toggle?id=
    private (int, string) HandleProfileToggle(HttpListenerRequest request)
    {
        var id = request.QueryString["id"];
        if (string.IsNullOrEmpty(id))
            return Json(400, new { error = "Missing id parameter" });

        var profile = _profiles.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null)
            return Json(404, new { error = "Profile not found" });

        _profiles.UpdateProfile(profile with { IsEnabled = !profile.IsEnabled });
        return Json(200, new { id, is_enabled = !profile.IsEnabled });
    }

    // POST /v1/dictation/start
    private async Task<(int, string)> HandleDictationStart()
    {
        if (_dictation.IsRecording)
            return Json(409, new { error = "Already recording" });

        await Application.Current.Dispatcher.InvokeAsync(() => _dictation.StartRecordingAsync());
        return Json(200, new { started = true });
    }

    // POST /v1/dictation/stop
    private async Task<(int, string)> HandleDictationStop()
    {
        if (!_dictation.IsRecording)
            return Json(409, new { error = "Not recording" });

        await Application.Current.Dispatcher.InvokeAsync(() => _dictation.StopRecordingAsync());
        return Json(200, new { stopped = true });
    }

    // GET /v1/dictation/status
    private (int, string) HandleDictationStatus() => Json(200, new
    {
        state = _dictation.State.ToString().ToLowerInvariant(),
        is_recording = _dictation.IsRecording,
        active_model = _modelManager.ActiveModelId,
        active_profile = _dictation.ActiveProfileName
    });

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _cts?.Dispose();
            _disposed = true;
        }
    }

    private Func<string, string>? GetVocabularyBooster() =>
        _settings.Current.VocabularyBoostingEnabled ? _vocabularyBoosting.Apply : null;
}
