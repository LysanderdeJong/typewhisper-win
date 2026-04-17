using System.IO;
using System.Diagnostics;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.ML.OnnxRuntime;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public partial class FileTranscriptionViewModel : ObservableObject
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;
    private readonly FileVocalIsolationService _vocalIsolation;
    private readonly FileSpeechSegmentationService _speechSegmentation;
    private readonly FileSpeakerDiarizationService _speakerDiarization;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly IPostProcessingPipeline _pipeline;

    private CancellationTokenSource? _cts;
    private TranscriptionResult? _lastResult;

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusText = Loc.Instance["FileTranscription.StatusDefault"];
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string? _detectedLanguage;
    [ObservableProperty] private double _processingTime;
    [ObservableProperty] private double _audioDuration;
    [ObservableProperty] private bool _useVocalIsolation;
    [ObservableProperty] private bool _useVoiceActivityDetection = true;
    [ObservableProperty] private bool _useSpeakerDiarization;

    public FileTranscriptionViewModel(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        FileVocalIsolationService vocalIsolation,
        FileSpeechSegmentationService speechSegmentation,
        FileSpeakerDiarizationService speakerDiarization,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
        _vocalIsolation = vocalIsolation;
        _speechSegmentation = speechSegmentation;
        _speakerDiarization = speakerDiarization;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;

        UseVocalIsolation = settings.Current.FileTranscriptionVocalIsolationEnabled;
        UseVoiceActivityDetection = settings.Current.FileTranscriptionVadEnabled;
        UseSpeakerDiarization = settings.Current.FileTranscriptionSpeakerDiarizationEnabled;
    }

    [RelayCommand]
    private async Task TranscribeFile(string? path)
    {
        var filePath = path ?? FilePath;
        if (string.IsNullOrEmpty(filePath)) return;

        if (!AudioFileService.IsSupported(filePath))
        {
            StatusText = Loc.Instance["FileTranscription.UnsupportedFormat"];
            return;
        }

        if (!_modelManager.Engine.IsModelLoaded)
        {
            StatusText = Loc.Instance["Status.NoModelLoaded"];
            return;
        }

        FilePath = filePath;
        IsProcessing = true;
        HasResult = false;
        ResultText = "";
        StatusText = Loc.Instance["FileTranscription.LoadingAudio"];

        _cts?.Cancel();
        _cts?.Dispose();
        using var currentCts = new CancellationTokenSource();
        _cts = currentCts;
        string? preparedAudioPath = null;

        try
        {
            var useSpeechSegmentation = FileTranscriptionMemoryPolicy.UsesSpeechSegmentation(
                UseVoiceActivityDetection,
                UseSpeakerDiarization);
            float[]? samples = null;
            var useFileBackedTranscription = FileTranscriptionMemoryPolicy.ShouldUseFileBackedTranscription(
                AudioFileService.GetDuration(filePath).TotalSeconds,
                UseSpeakerDiarization);
            var transcriptionAudioPath = filePath;

            if (UseVocalIsolation)
            {
                var vocalIsolationProgress = new Progress<FileVocalIsolationProgress>(progress =>
                {
                    StatusText = GetVocalIsolationStatusText(progress);
                });

                if (useSpeechSegmentation || useFileBackedTranscription)
                {
                    preparedAudioPath = await _vocalIsolation.CreateIsolatedAudioFileAsync(filePath, vocalIsolationProgress, currentCts.Token);
                    await _vocalIsolation.ReleaseLoadedModelAsync(currentCts.Token);
                    transcriptionAudioPath = preparedAudioPath;
                }
                else
                {
                    samples = await _vocalIsolation.IsolateVocalsForTranscriptionAsync(filePath, vocalIsolationProgress, currentCts.Token);
                }
            }

            var totalDuration = samples is not null
                ? samples.Length / 16000.0
                : AudioFileService.GetDuration(transcriptionAudioPath).TotalSeconds;
            var pipelineOptions = new PipelineOptions
            {
                VocabularyBooster = GetVocabularyBooster(),
                DictionaryCorrector = _dictionary.ApplyCorrections
            };

            var s = _settings.Current;
            var language = s.Language == "auto" ? null : s.Language;
            var task = s.TranscriptionTask == "translate"
                ? TranscriptionTask.Translate
                : TranscriptionTask.Transcribe;
            IReadOnlyList<DiarizedSpeakerSegment> diarizedSpeakers = [];

            if (UseSpeakerDiarization)
            {
                samples ??= await _audioFile.LoadAudioAsync(transcriptionAudioPath, currentCts.Token);
                var diarizationProgress = new Progress<FileSpeakerDiarizationProgress>(progress =>
                {
                    StatusText = GetDiarizationStatusText(progress);
                });
                diarizedSpeakers = await _speakerDiarization.DiarizeAsync(samples, diarizationProgress, currentCts.Token);
            }

            TranscriptionResult result;
            if (useSpeechSegmentation)
            {
                StatusText = Loc.Instance["FileTranscription.DetectingSpeech"];

                if (samples is null)
                {
                    result = await TranscribeSpeechSegmentsAsync(
                        _speechSegmentation.StreamSegmentsAsync(transcriptionAudioPath, currentCts.Token),
                        diarizedSpeakers,
                        language,
                        task,
                        pipelineOptions,
                        totalDuration,
                        currentCts.Token);
                }
                else
                {
                    var speechSegments = await LoadSpeechSegmentsAsync(transcriptionAudioPath, samples, currentCts.Token);
                    result = await TranscribeSpeechSegmentsAsync(speechSegments, diarizedSpeakers, language, task, pipelineOptions, totalDuration, currentCts.Token);
                }
            }
            else
            {
                if (useFileBackedTranscription && samples is null)
                {
                    StatusText = Loc.Instance["FileTranscription.Transcribing"];
                    result = await TranscribeSpeechSegmentsAsync(
                        _audioFile.StreamAudioChunksAsync(
                            transcriptionAudioPath,
                            FileTranscriptionMemoryPolicy.FileBackedTranscriptionChunkSamples,
                            currentCts.Token),
                        diarizedSpeakers,
                        language,
                        task,
                        pipelineOptions,
                        totalDuration,
                        currentCts.Token);
                }
                else
                {
                    samples ??= await _audioFile.LoadAudioAsync(transcriptionAudioPath, currentCts.Token);
                    StatusText = Loc.Instance["FileTranscription.Transcribing"];
                    var rawResult = await _modelManager.Engine.TranscribeAsync(samples, language, task, currentCts.Token);
                    result = await ApplyPipelineAsync(rawResult, pipelineOptions, currentCts.Token);
                }
            }

            _lastResult = result;

            ResultText = result.Text;
            DetectedLanguage = result.DetectedLanguage;
            ProcessingTime = result.ProcessingTime;
            AudioDuration = result.Duration;
            HasResult = true;
            StatusText = Loc.Instance.GetString("FileTranscription.DoneFormat", result.ProcessingTime, result.Duration);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Instance["Status.Cancelled"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
        }
        finally
        {
            if (preparedAudioPath is not null)
                TryDeleteTemporaryFile(preparedAudioPath);

            if (ReferenceEquals(_cts, currentCts))
                _cts = null;

            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task RedownloadSpeakerModels()
    {
        if (IsProcessing)
            return;

        IsProcessing = true;
        _cts?.Cancel();
        _cts?.Dispose();
        using var currentCts = new CancellationTokenSource();
        _cts = currentCts;

        try
        {
            var progress = new Progress<FileSpeakerDiarizationProgress>(p =>
            {
                StatusText = GetDiarizationStatusText(p);
            });

            await _speakerDiarization.RedownloadModelsAsync(progress, currentCts.Token);
            StatusText = Loc.Instance["FileTranscription.SpeakerModelsReady"];
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Instance["Status.Cancelled"];
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, currentCts))
                _cts = null;

            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task RedownloadVocalIsolationModel()
    {
        if (IsProcessing)
            return;

        IsProcessing = true;
        _cts?.Cancel();
        _cts?.Dispose();
        using var currentCts = new CancellationTokenSource();
        _cts = currentCts;

        try
        {
            var progress = new Progress<FileVocalIsolationProgress>(p =>
            {
                StatusText = GetVocalIsolationStatusText(p);
            });

            await _vocalIsolation.RedownloadModelAsync(progress, currentCts.Token);
            StatusText = Loc.Instance.GetString("FileTranscription.VocalIsolationModelReadyFormat", _vocalIsolation.CurrentExecutionProvider);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Instance["Status.Cancelled"];
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or OnnxRuntimeException)
        {
            StatusText = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, currentCts))
                _cts = null;

            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        if (!string.IsNullOrEmpty(ResultText))
            System.Windows.Clipboard.SetText(ResultText);
    }

    [RelayCommand]
    private void ExportSrt()
    {
        if (_lastResult?.Segments is not { Count: > 0 }) return;
        ExportFile("srt", SubtitleExporter.ToSrt(_lastResult.Segments));
    }

    [RelayCommand]
    private void ExportWebVtt()
    {
        if (_lastResult?.Segments is not { Count: > 0 }) return;
        ExportFile("vtt", SubtitleExporter.ToWebVtt(_lastResult.Segments));
    }

    [RelayCommand]
    private void ExportText()
    {
        if (string.IsNullOrEmpty(ResultText)) return;
        ExportFile("txt", ResultText);
    }

    private void ExportFile(string extension, string content)
    {
        var baseName = FilePath is not null ? Path.GetFileNameWithoutExtension(FilePath) : "transcription";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{baseName}.{extension}",
            Filter = extension.ToUpperInvariant() + $" Files|*.{extension}|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, content);
            StatusText = Loc.Instance.GetString("FileTranscription.ExportedFormat", Path.GetFileName(dialog.FileName));
        }
    }

    public void HandleFileDrop(string[] files)
    {
        if (files.Length > 0 && AudioFileService.IsSupported(files[0]))
        {
            TranscribeFileCommand.Execute(files[0]);
        }
    }

    private async Task<TranscriptionResult> ApplyPipelineAsync(
        TranscriptionResult rawResult,
        PipelineOptions options,
        CancellationToken cancellationToken)
    {
        if (rawResult.Segments.Count > 0)
        {
            var processedSegments = new List<TranscriptionSegment>(rawResult.Segments.Count);
            foreach (var segment in rawResult.Segments)
            {
                var processed = await _pipeline.ProcessAsync(segment.Text, options, cancellationToken);
                var text = processed.Text.Trim();
                if (text.Length == 0)
                    continue;

                processedSegments.Add(new TranscriptionSegment(text, segment.Start, segment.End));
            }

            return new TranscriptionResult
            {
                Text = string.Join(" ", processedSegments.Select(segment => segment.Text)),
                DetectedLanguage = rawResult.DetectedLanguage,
                Duration = rawResult.Duration,
                ProcessingTime = rawResult.ProcessingTime,
                NoSpeechProbability = rawResult.NoSpeechProbability,
                Segments = processedSegments,
            };
        }

        var pipelineResult = await _pipeline.ProcessAsync(rawResult.Text, options, cancellationToken);
        return new TranscriptionResult
        {
            Text = pipelineResult.Text,
            DetectedLanguage = rawResult.DetectedLanguage,
            Duration = rawResult.Duration,
            ProcessingTime = rawResult.ProcessingTime,
            NoSpeechProbability = rawResult.NoSpeechProbability,
            Segments = rawResult.Segments,
        };
    }

    private async Task<IReadOnlyList<AudioSpeechSegment>> LoadSpeechSegmentsAsync(
        string transcriptionAudioPath,
        float[]? preloadedSamples,
        CancellationToken cancellationToken)
    {
        var loadedSamples = preloadedSamples ?? await _audioFile.LoadAudioAsync(transcriptionAudioPath, cancellationToken);
        return await _speechSegmentation.SegmentAsync(loadedSamples, cancellationToken);
    }

    private async Task<TranscriptionResult> TranscribeSpeechSegmentsAsync(
        IReadOnlyList<AudioSpeechSegment> speechSegments,
        IReadOnlyList<DiarizedSpeakerSegment> diarizedSpeakers,
        string? language,
        TranscriptionTask task,
        PipelineOptions options,
        double totalDuration,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string? detectedLanguage = null;
        var processedSegments = new List<TranscriptionSegment>();

        for (var i = 0; i < speechSegments.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusText = Loc.Instance.GetString("FileTranscription.TranscribingSegmentFormat", i + 1, speechSegments.Count);

            var speechSegment = speechSegments[i];
            try
            {
                var rawResult = await _modelManager.Engine.TranscribeAsync(speechSegment.Samples, language, task, cancellationToken);
                detectedLanguage ??= rawResult.DetectedLanguage;

                if (rawResult.Segments.Count > 0)
                {
                    foreach (var rawSegment in rawResult.Segments)
                    {
                        var processed = await _pipeline.ProcessAsync(rawSegment.Text, options, cancellationToken);
                        var text = processed.Text.Trim();
                        if (text.Length == 0)
                            continue;

                        var (start, end) = ClampToSpeechSegment(
                            speechSegment,
                            speechSegment.StartSeconds + rawSegment.Start,
                            speechSegment.StartSeconds + rawSegment.End);

                        processedSegments.Add(new TranscriptionSegment(
                            text,
                            start,
                            end));
                    }

                    continue;
                }

                var pipelineResult = await _pipeline.ProcessAsync(rawResult.Text, options, cancellationToken);
                var segmentText = pipelineResult.Text.Trim();
                if (segmentText.Length == 0)
                    continue;

                processedSegments.Add(new TranscriptionSegment(segmentText, speechSegment.StartSeconds, speechSegment.EndSeconds));
            }
            finally
            {
                speechSegment.ReleaseSamples();
            }
        }

        stopwatch.Stop();

        var (labeledSegments, speakerIds, hasSpeakerLabels) = LabelSegments(processedSegments, diarizedSpeakers);

        return new TranscriptionResult
        {
            Text = CombineSegmentsText(labeledSegments, speakerIds, hasSpeakerLabels),
            DetectedLanguage = detectedLanguage,
            Duration = totalDuration,
            ProcessingTime = stopwatch.Elapsed.TotalSeconds,
            Segments = labeledSegments,
        };
    }

    private async Task<TranscriptionResult> TranscribeSpeechSegmentsAsync(
        IAsyncEnumerable<AudioSpeechSegment> speechSegments,
        IReadOnlyList<DiarizedSpeakerSegment> diarizedSpeakers,
        string? language,
        TranscriptionTask task,
        PipelineOptions options,
        double totalDuration,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string? detectedLanguage = null;
        var processedSegments = new List<TranscriptionSegment>();
        var segmentIndex = 0;

        await foreach (var speechSegment in speechSegments.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentIndex++;
            StatusText = Loc.Instance.GetString("FileTranscription.TranscribingSegmentFormat", segmentIndex, "...");

            try
            {
                var rawResult = await _modelManager.Engine.TranscribeAsync(speechSegment.Samples, language, task, cancellationToken);
                detectedLanguage ??= rawResult.DetectedLanguage;

                if (rawResult.Segments.Count > 0)
                {
                    foreach (var rawSegment in rawResult.Segments)
                    {
                        var processed = await _pipeline.ProcessAsync(rawSegment.Text, options, cancellationToken);
                        var text = processed.Text.Trim();
                        if (text.Length == 0)
                            continue;

                        var (start, end) = ClampToSpeechSegment(
                            speechSegment,
                            speechSegment.StartSeconds + rawSegment.Start,
                            speechSegment.StartSeconds + rawSegment.End);

                        processedSegments.Add(new TranscriptionSegment(text, start, end));
                    }

                    continue;
                }

                var pipelineResult = await _pipeline.ProcessAsync(rawResult.Text, options, cancellationToken);
                var segmentText = pipelineResult.Text.Trim();
                if (segmentText.Length == 0)
                    continue;

                processedSegments.Add(new TranscriptionSegment(segmentText, speechSegment.StartSeconds, speechSegment.EndSeconds));
            }
            finally
            {
                speechSegment.ReleaseSamples();
            }
        }

        stopwatch.Stop();

        var (labeledSegments, speakerIds, hasSpeakerLabels) = LabelSegments(processedSegments, diarizedSpeakers);

        return new TranscriptionResult
        {
            Text = CombineSegmentsText(labeledSegments, speakerIds, hasSpeakerLabels),
            DetectedLanguage = detectedLanguage,
            Duration = totalDuration,
            ProcessingTime = stopwatch.Elapsed.TotalSeconds,
            Segments = labeledSegments,
        };
    }

    private static (List<TranscriptionSegment> Segments, List<int?> SpeakerIds, bool HasSpeakerLabels) LabelSegments(
        IReadOnlyList<TranscriptionSegment> segments,
        IReadOnlyList<DiarizedSpeakerSegment> diarizedSpeakers)
    {
        if (segments.Count == 0 || diarizedSpeakers.Count == 0)
            return (segments.ToList(), Enumerable.Repeat<int?>(null, segments.Count).ToList(), false);

        var labeled = new List<TranscriptionSegment>(segments.Count);
        var speakerIds = new List<int?>(segments.Count);
        var hasSpeakerLabels = false;
        foreach (var segment in segments)
        {
            var speaker = FindBestSpeaker(segment, diarizedSpeakers);
            var text = speaker is null
                ? segment.Text
                : Loc.Instance.GetString("FileTranscription.SpeakerLabelFormat", speaker.Value + 1, segment.Text);

            hasSpeakerLabels |= speaker is not null;
            speakerIds.Add(speaker);
            labeled.Add(new TranscriptionSegment(text, segment.Start, segment.End));
        }

        return (labeled, speakerIds, hasSpeakerLabels);
    }

    internal static string CombineSegmentsText(
        IReadOnlyList<TranscriptionSegment> segments,
        IReadOnlyList<int?> speakerIds,
        bool useLineBreaks)
    {
        if (!useLineBreaks)
            return string.Join(" ", segments.Select(segment => segment.Text));

        var lines = new List<string>();
        var groupedTexts = new List<string>();
        int? currentSpeaker = null;
        string? currentSpeakerLabel = null;

        void FlushSpeakerLine()
        {
            if (currentSpeaker is null || groupedTexts.Count == 0)
                return;

            lines.Add(currentSpeakerLabel is { Length: > 0 }
                ? currentSpeakerLabel + " " + string.Join(" ", groupedTexts)
                : string.Join(" ", groupedTexts));
            groupedTexts.Clear();
            currentSpeaker = null;
            currentSpeakerLabel = null;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var speakerId = i < speakerIds.Count ? speakerIds[i] : null;
            if (speakerId is null)
            {
                FlushSpeakerLine();
                lines.Add(segments[i].Text);
                continue;
            }

            if (currentSpeaker == speakerId)
            {
                groupedTexts.Add(GetSegmentTextWithoutSpeakerLabel(segments[i].Text));
                continue;
            }

            FlushSpeakerLine();
            currentSpeaker = speakerId;
            currentSpeakerLabel = GetSpeakerLabelPrefix(segments[i].Text);
            groupedTexts.Add(GetSegmentTextWithoutSpeakerLabel(segments[i].Text));
        }

        FlushSpeakerLine();
        return string.Join(Environment.NewLine, lines);
    }

    private static string GetSegmentTextWithoutSpeakerLabel(string text)
    {
        var separatorIndex = text.IndexOf(':');
        return separatorIndex >= 0 && separatorIndex + 1 < text.Length
            ? text[(separatorIndex + 1)..].TrimStart()
            : text;
    }

    private static string? GetSpeakerLabelPrefix(string text)
    {
        var separatorIndex = text.IndexOf(':');
        return separatorIndex >= 0
            ? text[..(separatorIndex + 1)].TrimEnd()
            : null;
    }

    private static int? FindBestSpeaker(
        TranscriptionSegment segment,
        IReadOnlyList<DiarizedSpeakerSegment> diarizedSpeakers)
    {
        var bestSpeaker = default(int?);
        var bestOverlap = 0.0;

        foreach (var diarized in diarizedSpeakers)
        {
            var overlap = Math.Min(segment.End, diarized.EndSeconds) - Math.Max(segment.Start, diarized.StartSeconds);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestSpeaker = diarized.SpeakerId;
            }
        }

        return bestSpeaker;
    }

    private static (double Start, double End) ClampToSpeechSegment(AudioSpeechSegment speechSegment, double start, double end)
    {
        var clampedStart = Math.Clamp(start, speechSegment.StartSeconds, speechSegment.EndSeconds);
        var clampedEnd = Math.Clamp(end, clampedStart, speechSegment.EndSeconds);
        return (clampedStart, clampedEnd);
    }

    partial void OnUseVoiceActivityDetectionChanged(bool value)
    {
        if (_settings.Current.FileTranscriptionVadEnabled == value)
            return;

        _settings.Save(_settings.Current with { FileTranscriptionVadEnabled = value });
    }

    partial void OnUseSpeakerDiarizationChanged(bool value)
    {
        if (_settings.Current.FileTranscriptionSpeakerDiarizationEnabled == value)
            return;

        _settings.Save(_settings.Current with { FileTranscriptionSpeakerDiarizationEnabled = value });
    }

    partial void OnUseVocalIsolationChanged(bool value)
    {
        if (_settings.Current.FileTranscriptionVocalIsolationEnabled == value)
            return;

        _settings.Save(_settings.Current with { FileTranscriptionVocalIsolationEnabled = value });
    }

    private static string GetDiarizationStatusText(FileSpeakerDiarizationProgress progress) => progress.Stage switch
    {
        FileSpeakerDiarizationStage.PreparingModels => Loc.Instance["FileTranscription.PreparingSpeakerModels"],
        FileSpeakerDiarizationStage.DownloadingSegmentationModel => GetDownloadStatusText("FileTranscription.DownloadingSpeakerSegmentationModelFormat", progress.Fraction),
        FileSpeakerDiarizationStage.ExtractingSegmentationModel => Loc.Instance["FileTranscription.ExtractingSpeakerSegmentationModel"],
        FileSpeakerDiarizationStage.DownloadingEmbeddingModel => GetDownloadStatusText("FileTranscription.DownloadingSpeakerEmbeddingModelFormat", progress.Fraction),
        _ => Loc.Instance["FileTranscription.DiarizingSpeakers"],
    };

    private static string GetVocalIsolationStatusText(FileVocalIsolationProgress progress) => progress.Stage switch
    {
        FileVocalIsolationStage.PreparingModel => Loc.Instance["FileTranscription.PreparingVocalIsolationModel"],
        FileVocalIsolationStage.DownloadingModel => GetDownloadStatusText("FileTranscription.DownloadingVocalIsolationModelFormat", progress.Fraction),
        _ => progress.ExecutionProvider is string provider && !string.IsNullOrWhiteSpace(provider)
            ? progress.Fraction is double fractionWithProvider
                ? Loc.Instance.GetString("FileTranscription.IsolatingVocalsWithProviderFormat", provider, fractionWithProvider)
                : Loc.Instance.GetString("FileTranscription.IsolatingVocalsWithProvider", provider)
            : progress.Fraction is double fraction
                ? Loc.Instance.GetString("FileTranscription.IsolatingVocalsFormat", fraction)
                : Loc.Instance["FileTranscription.IsolatingVocals"],
    };

    private static string GetDownloadStatusText(string formatKey, double? fraction) =>
        fraction is double value
            ? Loc.Instance.GetString(formatKey, value)
            : Loc.Instance[formatKey.Replace("Format", string.Empty)];

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private Func<string, string>? GetVocabularyBooster() =>
        _settings.Current.VocabularyBoostingEnabled ? _vocabularyBoosting.Apply : null;
}
