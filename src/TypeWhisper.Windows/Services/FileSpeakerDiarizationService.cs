using System.IO;
using System.Net.Http;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Readers;
using SherpaOnnx;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

public enum FileSpeakerDiarizationStage
{
    PreparingModels,
    DownloadingSegmentationModel,
    ExtractingSegmentationModel,
    DownloadingEmbeddingModel,
    AnalyzingSpeakers,
}

public readonly record struct FileSpeakerDiarizationProgress(FileSpeakerDiarizationStage Stage, double? Fraction = null);

public sealed record DiarizedSpeakerSegment(double StartSeconds, double EndSeconds, int SpeakerId);

public sealed class FileSpeakerDiarizationService : IDisposable
{
    private const string SegmentationArchiveUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2";
    private const string EmbeddingModelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";
    private const string SegmentationFolderName = "sherpa-onnx-pyannote-segmentation-3-0";
    private const string EmbeddingFileName = "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";
    private const int SampleRate = 16000;
    private readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _modelGate = new(1, 1);

    public async Task<IReadOnlyList<DiarizedSpeakerSegment>> DiarizeAsync(
        float[] samples,
        IProgress<FileSpeakerDiarizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await PrepareModelsAsync(progress, cancellationToken);
        progress?.Report(new FileSpeakerDiarizationProgress(FileSpeakerDiarizationStage.AnalyzingSpeakers));

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = GetSegmentationModelPath();
            config.Embedding.Model = GetEmbeddingModelPath();
            config.Clustering.Threshold = 0.5f;

            using var diarization = new OfflineSpeakerDiarization(config);
            if (diarization.SampleRate != SampleRate)
                throw new InvalidOperationException($"Expected diarization sample rate {diarization.SampleRate}, got {SampleRate}.");

            var segments = diarization.Process(samples);
            cancellationToken.ThrowIfCancellationRequested();

            return segments
                .Select(segment => new DiarizedSpeakerSegment(segment.Start, segment.End, segment.Speaker))
                .ToList();
        }, cancellationToken);
    }

    public async Task PrepareModelsAsync(
        IProgress<FileSpeakerDiarizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new FileSpeakerDiarizationProgress(FileSpeakerDiarizationStage.PreparingModels));
        await EnsureModelsAsync(progress, forceRedownload: false, cancellationToken);
    }

    public async Task RedownloadModelsAsync(
        IProgress<FileSpeakerDiarizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new FileSpeakerDiarizationProgress(FileSpeakerDiarizationStage.PreparingModels));
        await EnsureModelsAsync(progress, forceRedownload: true, cancellationToken);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _modelGate.Dispose();
    }

    private async Task EnsureModelsAsync(
        IProgress<FileSpeakerDiarizationProgress>? progress,
        bool forceRedownload,
        CancellationToken cancellationToken)
        => await EnsureModelsAsync(progress, forceRedownload, attemptedRecovery: false, cancellationToken);

    private async Task EnsureModelsAsync(
        IProgress<FileSpeakerDiarizationProgress>? progress,
        bool forceRedownload,
        bool attemptedRecovery,
        CancellationToken cancellationToken)
    {
        var shouldRetry = false;

        await _modelGate.WaitAsync(cancellationToken);
        try
        {
            if (forceRedownload)
                TryDeleteDirectory(GetModelsRoot());

            if (File.Exists(GetSegmentationModelPath()) && File.Exists(GetEmbeddingModelPath()))
                return;

            Directory.CreateDirectory(GetModelsRoot());

            if (!File.Exists(GetSegmentationModelPath()))
            {
                var archivePath = Path.Combine(GetModelsRoot(), "speaker-segmentation.tar.bz2");
                progress?.Report(new FileSpeakerDiarizationProgress(FileSpeakerDiarizationStage.DownloadingSegmentationModel));
                await DownloadFileAsync(SegmentationArchiveUrl, archivePath, FileSpeakerDiarizationStage.DownloadingSegmentationModel, progress, cancellationToken);
                progress?.Report(new FileSpeakerDiarizationProgress(FileSpeakerDiarizationStage.ExtractingSegmentationModel));
                ExtractSegmentationArchive(archivePath, cancellationToken);
                TryDelete(archivePath);
            }

            if (!File.Exists(GetEmbeddingModelPath()))
            {
                progress?.Report(new FileSpeakerDiarizationProgress(FileSpeakerDiarizationStage.DownloadingEmbeddingModel));
                await DownloadFileAsync(EmbeddingModelUrl, GetEmbeddingModelPath(), FileSpeakerDiarizationStage.DownloadingEmbeddingModel, progress, cancellationToken);
            }
        }
        catch when (!attemptedRecovery)
        {
            TryDeleteDirectory(GetModelsRoot());
            shouldRetry = true;
        }
        finally
        {
            _modelGate.Release();
        }

        if (shouldRetry)
            await EnsureModelsAsync(progress, forceRedownload: false, attemptedRecovery: true, cancellationToken);
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        FileSpeakerDiarizationStage stage,
        IProgress<FileSpeakerDiarizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long totalBytesRead = 0;
                var lastReportedPercent = -1;
                int bytesRead;

                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesRead += bytesRead;

                    double? fraction = contentLength is > 0
                        ? Math.Clamp(totalBytesRead / (double)contentLength.Value, 0, 1)
                        : null;
                    if (fraction is double value)
                    {
                        var percent = (int)Math.Round(value * 100, MidpointRounding.AwayFromZero);
                        if (percent > lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            progress?.Report(new FileSpeakerDiarizationProgress(stage, value));
                        }
                    }
                }

                if (contentLength is > 0 && lastReportedPercent < 100)
                    progress?.Report(new FileSpeakerDiarizationProgress(stage, 1.0));

                await destination.FlushAsync(cancellationToken);
            }

            if (File.Exists(destinationPath))
                File.Delete(destinationPath);

            File.Move(tempPath, destinationPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void ExtractSegmentationArchive(string archivePath, CancellationToken cancellationToken)
    {
        var modelsRoot = GetModelsRoot();
        var finalSegmentationDirectory = GetSegmentationDirectoryPath();
        var tempRoot = Path.Combine(modelsRoot, "extract-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempRoot);
        try
        {
            ExtractArchive(archivePath, tempRoot, cancellationToken);

            var extractedSegmentationDirectory = Path.Combine(tempRoot, SegmentationFolderName);
            if (!Directory.Exists(extractedSegmentationDirectory))
                throw new InvalidOperationException($"Segmentation archive did not contain '{SegmentationFolderName}'.");

            if (Directory.Exists(finalSegmentationDirectory))
                Directory.Delete(finalSegmentationDirectory, recursive: true);

            Directory.Move(extractedSegmentationDirectory, finalSegmentationDirectory);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ExtractArchive(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var bzipStream = new BZip2Stream(fileStream, CompressionMode.Decompress, false);
        using var reader = ReaderFactory.Open(bzipStream);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory)
                continue;

            reader.WriteEntryToDirectory(destinationDirectory, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true,
            });
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static string GetModelsRoot() => Path.Combine(TypeWhisperEnvironment.DataPath, "FileTranscription", "Diarization");
    private static string GetSegmentationDirectoryPath() => Path.Combine(GetModelsRoot(), SegmentationFolderName);
    private static string GetSegmentationModelPath() => Path.Combine(GetModelsRoot(), SegmentationFolderName, "model.onnx");
    private static string GetEmbeddingModelPath() => Path.Combine(GetModelsRoot(), EmbeddingFileName);
}
