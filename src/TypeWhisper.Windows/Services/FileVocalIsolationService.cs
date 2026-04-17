using System.Buffers;
using System.IO;
using System.Net.Http;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Wave;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

public enum FileVocalIsolationStage
{
    PreparingModel,
    DownloadingModel,
    IsolatingVocals,
}

public readonly record struct FileVocalIsolationProgress(FileVocalIsolationStage Stage, double? Fraction = null, string? ExecutionProvider = null);
internal readonly record struct StreamingIsolationResult(string FilePath, int TargetFrames);

public sealed class FileVocalIsolationService : IDisposable
{
    public const string DirectMlDeviceIdEnvironmentVariable = "TYPEWHISPER_DML_DEVICE_ID";
    public const string CpuMemArenaEnvironmentVariable = "TYPEWHISPER_ONNX_CPU_MEM_ARENA";
    public const string GraphOptimizationLevelEnvironmentVariable = "TYPEWHISPER_ONNX_GRAPH_OPT";
    private const string ModelUrl = "https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/Kim_Vocal_2.onnx";
    private const string ModelFileName = "Kim_Vocal_2.onnx";
    private const int SourceSampleRate = 44100;
    private const int SourceChannels = 2;
    private const int OutputSampleRate = 16000;
    private const int OutputChannels = 1;
    private const int Nfft = 7680;
    private const int HopLength = 1024;
    private const int TrimSamples = Nfft / 2;
    private const int ModelFrequencyBins = 3072;
    private const int ModelFrames = 256;
    private const int ChunkSamples = HopLength * (ModelFrames - 1);
    private const int GeneratedSamples = ChunkSamples - (TrimSamples * 2);
    private const int FullFrequencyBins = Nfft / 2 + 1;
    private const int InferenceBatchSize = 4;
    private const int DecodeBufferBytes = 65536;
    private const int MaxBufferedFrames = ChunkSamples + (GeneratedSamples * InferenceBatchSize) + TrimSamples;

    private readonly AudioFileService _audioFile;
    private readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly float[] _window = CreateHannWindow();
    private readonly DenseTensor<float>?[] _inputTensorCache = new DenseTensor<float>[InferenceBatchSize + 1];
    // Per-thread FFT scratch buffer. Fourier.Forward/Inverse operate on the full array length
    // (Nfft = 7680, ~61 KB of Complex32 = ~123 KB when counting both halves), so this buffer
    // cannot come from ArrayPool<T>.Shared (which rounds up to power-of-two buckets). Keeping
    // one per worker thread eliminates ~16 allocations per inference batch (8 STFT + 8 ISTFT,
    // 2 channels * 4 batch items) without introducing cross-thread aliasing.
    private readonly ThreadLocal<MathNet.Numerics.Complex32[]> _fftBuffer = new(() => new MathNet.Numerics.Complex32[Nfft]);
    private readonly int? _directMlDeviceId;

    private InferenceSession? _session;
    private string? _inputName;
    private string _executionProvider = "CPU";

    public FileVocalIsolationService(AudioFileService audioFile, int? directMlDeviceId = null)
    {
        _audioFile = audioFile;
        _directMlDeviceId = directMlDeviceId;
    }

    public string CurrentExecutionProvider => _executionProvider;

    public async Task<float[]> IsolateVocalsForTranscriptionAsync(
        string filePath,
        IProgress<FileVocalIsolationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var isolatedResult = await CreateIsolatedAudioFileResultAsync(filePath, progress, cancellationToken);
        try
        {
            var isolatedSamples = await Task.Run(() => LoadFloatWave(isolatedResult.FilePath, cancellationToken), cancellationToken);
            if (isolatedSamples.Length > isolatedResult.TargetFrames)
                Array.Resize(ref isolatedSamples, isolatedResult.TargetFrames);

            return isolatedSamples;
        }
        finally
        {
            TryDelete(isolatedResult.FilePath);
        }
    }

    public async Task<string> CreateIsolatedAudioFileAsync(
        string filePath,
        IProgress<FileVocalIsolationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var isolatedResult = await CreateIsolatedAudioFileResultAsync(filePath, progress, cancellationToken);
        return isolatedResult.FilePath;
    }

    private async Task<StreamingIsolationResult> CreateIsolatedAudioFileResultAsync(
        string filePath,
        IProgress<FileVocalIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var session = await GetOrLoadSessionAsync(progress, forceRedownload: false, cancellationToken);
        progress?.Report(new FileVocalIsolationProgress(FileVocalIsolationStage.IsolatingVocals, ExecutionProvider: _executionProvider));

        await _inferenceGate.WaitAsync(cancellationToken);
        try
        {
            var isolatedResult = await Task.Run(
                () => IsolateVocalsToTemporaryWaveFile(filePath, session, progress, cancellationToken),
                cancellationToken);

            return isolatedResult;
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    public async Task RedownloadModelAsync(
        IProgress<FileVocalIsolationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await GetOrLoadSessionAsync(progress, forceRedownload: true, cancellationToken);
    }

    public async Task ReleaseLoadedModelAsync(CancellationToken cancellationToken = default)
    {
        await _modelGate.WaitAsync(cancellationToken);
        try
        {
            await _inferenceGate.WaitAsync(cancellationToken);
            try
            {
                _session?.Dispose();
                _session = null;
                _inputName = null;
                Array.Clear(_inputTensorCache, 0, _inputTensorCache.Length);
            }
            finally
            {
                _inferenceGate.Release();
            }
        }
        finally
        {
            _modelGate.Release();
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _httpClient.Dispose();
        _modelGate.Dispose();
        _inferenceGate.Dispose();
        _fftBuffer.Dispose();
    }

    private async Task<InferenceSession> GetOrLoadSessionAsync(
        IProgress<FileVocalIsolationProgress>? progress,
        bool forceRedownload,
        CancellationToken cancellationToken)
    {
        await _modelGate.WaitAsync(cancellationToken);
        try
        {
            if (forceRedownload)
            {
                _session?.Dispose();
                _session = null;
                _inputName = null;
                TryDelete(Path.Join(GetModelsRoot(), ModelFileName));
            }

            if (_session is not null)
                return _session;

            progress?.Report(new FileVocalIsolationProgress(FileVocalIsolationStage.PreparingModel));

            var modelPath = Path.Join(GetModelsRoot(), ModelFileName);
            if (!File.Exists(modelPath))
                await DownloadModelAsync(modelPath, progress, cancellationToken);

            _session = await LoadSessionWithRecoveryAsync(modelPath, progress, forceRedownload, cancellationToken);
            _inputName = _session.InputMetadata.Keys.First();
            return _session;
        }
        finally
        {
            _modelGate.Release();
        }
    }

    private async Task<InferenceSession> LoadSessionWithRecoveryAsync(
        string modelPath,
        IProgress<FileVocalIsolationProgress>? progress,
        bool forceRedownload,
        CancellationToken cancellationToken)
    {
        try
        {
            return CreateSession(modelPath);
        }
        catch (OnnxRuntimeException) when (!forceRedownload)
        {
            TryDelete(modelPath);
            await DownloadModelAsync(modelPath, progress, cancellationToken);
            return CreateSession(modelPath);
        }
        catch (InvalidOperationException) when (!forceRedownload)
        {
            TryDelete(modelPath);
            await DownloadModelAsync(modelPath, progress, cancellationToken);
            return CreateSession(modelPath);
        }
        catch (BadImageFormatException) when (!forceRedownload)
        {
            TryDelete(modelPath);
            await DownloadModelAsync(modelPath, progress, cancellationToken);
            return CreateSession(modelPath);
        }
    }

    private async Task DownloadModelAsync(
        string destinationPath,
        IProgress<FileVocalIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(GetModelsRoot());
        var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            progress?.Report(new FileVocalIsolationProgress(FileVocalIsolationStage.DownloadingModel));

            using var response = await _httpClient.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long totalBytesRead = 0;
                var lastReportedPercent = -1;
                int bytesRead;

                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesRead += bytesRead;

                    if (contentLength is > 0)
                    {
                        var fraction = Math.Clamp(totalBytesRead / (double)contentLength.Value, 0, 1);
                        var percent = (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);
                        if (percent > lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            progress?.Report(new FileVocalIsolationProgress(FileVocalIsolationStage.DownloadingModel, fraction));
                        }
                    }
                }

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

    private InferenceSession CreateSession(string modelPath)
    {
        try
        {
            using var directMlOptions = CreateSessionOptions(useDirectMl: true);
            directMlOptions.EnableMemoryPattern = false;
            directMlOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            directMlOptions.AppendExecutionProvider_DML(GetDirectMlDeviceId());
            var directMlSession = new InferenceSession(modelPath, directMlOptions);
            _executionProvider = "DirectML";
            return directMlSession;
        }
        catch (OnnxRuntimeException)
        {
            using var cpuOptions = CreateSessionOptions(useDirectMl: false);
            var cpuSession = new InferenceSession(modelPath, cpuOptions);
            _executionProvider = "CPU";
            return cpuSession;
        }
        catch (DllNotFoundException)
        {
            using var cpuOptions = CreateSessionOptions(useDirectMl: false);
            var cpuSession = new InferenceSession(modelPath, cpuOptions);
            _executionProvider = "CPU";
            return cpuSession;
        }
        catch (EntryPointNotFoundException)
        {
            using var cpuOptions = CreateSessionOptions(useDirectMl: false);
            var cpuSession = new InferenceSession(modelPath, cpuOptions);
            _executionProvider = "CPU";
            return cpuSession;
        }
        catch (PlatformNotSupportedException)
        {
            using var cpuOptions = CreateSessionOptions(useDirectMl: false);
            var cpuSession = new InferenceSession(modelPath, cpuOptions);
            _executionProvider = "CPU";
            return cpuSession;
        }
        catch (BadImageFormatException)
        {
            using var cpuOptions = CreateSessionOptions(useDirectMl: false);
            var cpuSession = new InferenceSession(modelPath, cpuOptions);
            _executionProvider = "CPU";
            return cpuSession;
        }
    }

    private static SessionOptions CreateSessionOptions(bool useDirectMl)
    {
        return new SessionOptions
        {
            EnableCpuMemArena = GetCpuMemArenaEnabled(),
            GraphOptimizationLevel = GetGraphOptimizationLevel(),
            InterOpNumThreads = 1,
            IntraOpNumThreads = useDirectMl ? 1 : Environment.ProcessorCount,
        };
    }

    private float[] IsolateVocalsCore(
        float[] stereoSamples,
        InferenceSession session,
        IProgress<FileVocalIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (stereoSamples.Length == 0)
            return [];
        if (stereoSamples.Length % SourceChannels != 0)
            throw new InvalidOperationException("Expected interleaved stereo samples for vocal isolation.");

        var frameCount = stereoSamples.Length / SourceChannels;
        var left = new float[frameCount];
        var right = new float[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            left[i] = stereoSamples[i * SourceChannels];
            right[i] = stereoSamples[i * SourceChannels + 1];
        }

        var padSamples = (GeneratedSamples - (frameCount % GeneratedSamples)) % GeneratedSamples;
        var paddedLength = frameCount + (TrimSamples * 2) + padSamples;
        var leftPadded = new float[paddedLength];
        var rightPadded = new float[paddedLength];
        Array.Copy(left, 0, leftPadded, TrimSamples, frameCount);
        Array.Copy(right, 0, rightPadded, TrimSamples, frameCount);

        var outputLeft = new float[frameCount + padSamples];
        var outputRight = new float[frameCount + padSamples];
        var chunkOffsets = Enumerable.Range(0, ((paddedLength - ChunkSamples) / GeneratedSamples) + 1)
            .Select(index => index * GeneratedSamples)
            .ToArray();
        var totalChunks = chunkOffsets.Length;
        var processedChunks = 0;

        for (var batchStart = 0; batchStart < chunkOffsets.Length; batchStart += InferenceBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchCount = Math.Min(InferenceBatchSize, chunkOffsets.Length - batchStart);
            var leftChunks = new float[batchCount][];
            var rightChunks = new float[batchCount][];
            (float[] Left, float[] Right)[]? batchOutputs = null;
            try
            {
                for (var i = 0; i < batchCount; i++)
                {
                    var chunkOffset = chunkOffsets[batchStart + i];
                    leftChunks[i] = ArrayPool<float>.Shared.Rent(ChunkSamples);
                    rightChunks[i] = ArrayPool<float>.Shared.Rent(ChunkSamples);
                    Array.Copy(leftPadded, chunkOffset, leftChunks[i], 0, ChunkSamples);
                    Array.Copy(rightPadded, chunkOffset, rightChunks[i], 0, ChunkSamples);
                }

                batchOutputs = ProcessChunkBatch(session, leftChunks, rightChunks);
                for (var i = 0; i < batchOutputs.Length; i++)
                {
                    var writeOffset = (batchStart + i) * GeneratedSamples;
                    Array.Copy(batchOutputs[i].Left, TrimSamples, outputLeft, writeOffset, GeneratedSamples);
                    Array.Copy(batchOutputs[i].Right, TrimSamples, outputRight, writeOffset, GeneratedSamples);
                }

                processedChunks += batchCount;
                progress?.Report(new FileVocalIsolationProgress(
                    FileVocalIsolationStage.IsolatingVocals,
                    processedChunks / (double)totalChunks,
                    _executionProvider));
            }
            finally
            {
                for (var i = 0; i < batchCount; i++)
                {
                    if (leftChunks[i] is not null)
                        ArrayPool<float>.Shared.Return(leftChunks[i]);
                    if (rightChunks[i] is not null)
                        ArrayPool<float>.Shared.Return(rightChunks[i]);
                }

                if (batchOutputs is not null)
                {
                    for (var i = 0; i < batchOutputs.Length; i++)
                    {
                        if (batchOutputs[i].Left is { } l)
                            ArrayPool<float>.Shared.Return(l);
                        if (batchOutputs[i].Right is { } r)
                            ArrayPool<float>.Shared.Return(r);
                    }
                }
            }
        }

        if (padSamples > 0)
        {
            Array.Resize(ref outputLeft, frameCount);
            Array.Resize(ref outputRight, frameCount);
        }

        var interleaved = new float[frameCount * SourceChannels];
        for (var i = 0; i < frameCount; i++)
        {
            interleaved[i * SourceChannels] = outputLeft[i];
            interleaved[i * SourceChannels + 1] = outputRight[i];
        }

        return interleaved;
    }

    private StreamingIsolationResult IsolateVocalsToTemporaryWaveFile(
        string filePath,
        InferenceSession session,
        IProgress<FileVocalIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Join(GetModelsRoot(), "Temp");
        Directory.CreateDirectory(tempDirectory);

        var tempPath = Path.Join(tempDirectory, Guid.NewGuid().ToString("N") + ".wav");
        var succeeded = false;
        try
        {
            using var reader = new MediaFoundationReader(filePath);
            using var resampled = new MediaFoundationResampler(reader, new WaveFormat(SourceSampleRate, 16, SourceChannels))
            {
                ResamplerQuality = 60
            };
            using var writer = new WaveFileWriter(tempPath, WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels));
            var monoResampler = new StreamingMonoResampler(SourceSampleRate, OutputSampleRate);

            var expectedFrames = Math.Max(0L, (long)Math.Round(reader.TotalTime.TotalSeconds * SourceSampleRate));
            var expectedPadSamples = (GeneratedSamples - (expectedFrames % GeneratedSamples)) % GeneratedSamples;
            var totalChunks = Math.Max(1L, (expectedFrames + expectedPadSamples) / GeneratedSamples);

            var decodeBuffer = new byte[DecodeBufferBytes];
            var leftBuffer = new float[MaxBufferedFrames];
            var rightBuffer = new float[MaxBufferedFrames];
            var bufferedFrames = TrimSamples;
            long totalFramesRead = 0;
            long processedChunks = 0;
            var reachedEndOfInput = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                while (!reachedEndOfInput && bufferedFrames < ChunkSamples + ((InferenceBatchSize - 1) * GeneratedSamples))
                {
                    var bytesRead = resampled.Read(decodeBuffer, 0, decodeBuffer.Length);
                    if (bytesRead <= 0)
                    {
                        var actualPadSamples = (GeneratedSamples - (totalFramesRead % GeneratedSamples)) % GeneratedSamples;
                        var trailingPadding = TrimSamples + (int)actualPadSamples;
                        if (bufferedFrames + trailingPadding > MaxBufferedFrames)
                            throw new InvalidOperationException("Streaming vocal isolation buffer overflow.");

                        Array.Clear(leftBuffer, bufferedFrames, trailingPadding);
                        Array.Clear(rightBuffer, bufferedFrames, trailingPadding);
                        bufferedFrames += trailingPadding;
                        totalChunks = Math.Max(1L, (totalFramesRead + actualPadSamples) / GeneratedSamples);
                        reachedEndOfInput = true;
                        break;
                    }

                    var framesRead = bytesRead / (2 * SourceChannels);
                    if (bufferedFrames + framesRead > MaxBufferedFrames)
                        throw new InvalidOperationException("Streaming vocal isolation buffer overflow.");

                    // Reinterpret the decode buffer as shorts and deinterleave with direct indexing
                    // to avoid the per-call overhead of BitConverter.ToInt16.
                    var shortView = System.Runtime.InteropServices.MemoryMarshal
                        .Cast<byte, short>(decodeBuffer.AsSpan(0, framesRead * SourceChannels * 2));
                    const float scale = 1f / 32768f;
                    for (var frame = 0; frame < framesRead; frame++)
                    {
                        var sampleIndex = frame * SourceChannels;
                        leftBuffer[bufferedFrames + frame] = shortView[sampleIndex] * scale;
                        rightBuffer[bufferedFrames + frame] = shortView[sampleIndex + 1] * scale;
                    }

                    bufferedFrames += framesRead;
                    totalFramesRead += framesRead;
                }

                var availableChunks = bufferedFrames < ChunkSamples
                    ? 0
                    : 1 + ((bufferedFrames - ChunkSamples) / GeneratedSamples);
                if (availableChunks <= 0)
                {
                    if (reachedEndOfInput)
                        break;

                    continue;
                }

                var batchCount = Math.Min(InferenceBatchSize, availableChunks);
                var leftChunks = new float[batchCount][];
                var rightChunks = new float[batchCount][];
                (float[] Left, float[] Right)[]? batchOutputs = null;
                try
                {
                    for (var i = 0; i < batchCount; i++)
                    {
                        var chunkOffset = i * GeneratedSamples;
                        leftChunks[i] = ArrayPool<float>.Shared.Rent(ChunkSamples);
                        rightChunks[i] = ArrayPool<float>.Shared.Rent(ChunkSamples);
                        Array.Copy(leftBuffer, chunkOffset, leftChunks[i], 0, ChunkSamples);
                        Array.Copy(rightBuffer, chunkOffset, rightChunks[i], 0, ChunkSamples);
                    }

                    batchOutputs = ProcessChunkBatch(session, leftChunks, rightChunks);
                    for (var i = 0; i < batchCount; i++)
                    {
                        var batchOutput = batchOutputs[i];
                        monoResampler.WriteStereoChunk(writer, batchOutput.Left, batchOutput.Right, TrimSamples, GeneratedSamples);
                    }
                }
                finally
                {
                    for (var i = 0; i < batchCount; i++)
                    {
                        if (leftChunks[i] is not null)
                            ArrayPool<float>.Shared.Return(leftChunks[i]);
                        if (rightChunks[i] is not null)
                            ArrayPool<float>.Shared.Return(rightChunks[i]);
                    }

                    if (batchOutputs is not null)
                    {
                        for (var i = 0; i < batchOutputs.Length; i++)
                        {
                            if (batchOutputs[i].Left is { } l)
                                ArrayPool<float>.Shared.Return(l);
                            if (batchOutputs[i].Right is { } r)
                                ArrayPool<float>.Shared.Return(r);
                        }
                    }
                }

                var consumedFrames = batchCount * GeneratedSamples;
                var remainingFrames = bufferedFrames - consumedFrames;
                if (remainingFrames > 0)
                {
                    Array.Copy(leftBuffer, consumedFrames, leftBuffer, 0, remainingFrames);
                    Array.Copy(rightBuffer, consumedFrames, rightBuffer, 0, remainingFrames);
                }

                bufferedFrames = remainingFrames;
                processedChunks += batchCount;
                progress?.Report(new FileVocalIsolationProgress(
                    FileVocalIsolationStage.IsolatingVocals,
                    Math.Clamp(processedChunks / (double)totalChunks, 0, 1),
                    _executionProvider));
            }

            monoResampler.Complete(writer);
            writer.Flush();
            var targetFrames = Math.Max(1, (int)Math.Round(totalFramesRead * (double)OutputSampleRate / SourceSampleRate));
            succeeded = true;
            return new StreamingIsolationResult(tempPath, targetFrames);
        }
        finally
        {
            if (!succeeded)
                TryDelete(tempPath);
        }
    }

    private (float[] Left, float[] Right)[] ProcessChunkBatch(
        InferenceSession session,
        IReadOnlyList<float[]> leftChunks,
        IReadOnlyList<float[]> rightChunks)
    {
        var batchCount = leftChunks.Count;
        var inputTensor = GetOrCreateInputTensor(batchCount);
        inputTensor.Buffer.Span.Clear();
        Parallel.For(0, batchCount, batchIndex =>
        {
            CopyStftToTensor(inputTensor, batchIndex, 0, 1, leftChunks[batchIndex]);
            CopyStftToTensor(inputTensor, batchIndex, 2, 3, rightChunks[batchIndex]);
        });

        using var results = session.Run([
            NamedOnnxValue.CreateFromTensor(_inputName!, inputTensor)
        ]);

        var outputTensor = results.First().AsTensor<float>();
        var outputs = new (float[] Left, float[] Right)[batchCount];

        Parallel.For(0, batchCount, batchIndex =>
        {
            // Rent from the shared pool; the caller owns these buffers and must return them.
            var isolatedLeft = ArrayPool<float>.Shared.Rent(ChunkSamples);
            var isolatedRight = ArrayPool<float>.Shared.Rent(ChunkSamples);
            CopyIstftFromTensor(outputTensor, batchIndex, 0, 1, isolatedLeft, 0, TrimSamples, ChunkSamples);
            CopyIstftFromTensor(outputTensor, batchIndex, 2, 3, isolatedRight, 0, TrimSamples, ChunkSamples);
            outputs[batchIndex] = (isolatedLeft, isolatedRight);
        });

        return outputs;
    }

    private DenseTensor<float> GetOrCreateInputTensor(int batchCount)
    {
        if (batchCount <= 0 || batchCount > InferenceBatchSize)
            throw new ArgumentOutOfRangeException(nameof(batchCount));

        return _inputTensorCache[batchCount] ??= new DenseTensor<float>(new[] { batchCount, 4, ModelFrequencyBins, ModelFrames });
    }

    private void CopyStftToTensor(DenseTensor<float> inputTensor, int batchIndex, int realChannelIndex, int imaginaryChannelIndex, float[] samples)
    {
        // Pool the ~1 MB padded scratch buffer. MathNet's Fourier.Forward operates on the full
        // array length, so the FFT workspace has to match Nfft exactly and cannot come from
        // ArrayPool (which rounds up to power-of-two buckets) - it is shared per-thread via
        // _fftBuffer instead.
        var padded = ArrayPool<float>.Shared.Rent(ChunkSamples + Nfft);
        var fftBuffer = _fftBuffer.Value!;
        try
        {
            Array.Clear(padded, 0, ChunkSamples + Nfft);
            Array.Copy(samples, 0, padded, TrimSamples, ChunkSamples);

            for (var frame = 0; frame < ModelFrames; frame++)
            {
                var frameOffset = frame * HopLength;
                for (var i = 0; i < Nfft; i++)
                    fftBuffer[i] = new MathNet.Numerics.Complex32(padded[frameOffset + i] * _window[i], 0);

                Fourier.Forward(fftBuffer, FourierOptions.AsymmetricScaling);

                for (var bin = 0; bin < ModelFrequencyBins; bin++)
                {
                    inputTensor[batchIndex, realChannelIndex, bin, frame] = fftBuffer[bin].Real;
                    inputTensor[batchIndex, imaginaryChannelIndex, bin, frame] = fftBuffer[bin].Imaginary;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(padded);
        }
    }

    private void CopyIstftFromTensor(
        Tensor<float> outputTensor,
        int batchIndex,
        int realChannelIndex,
        int imaginaryChannelIndex,
        float[] destination,
        int destinationOffset,
        int sourceStart,
        int length)
    {
        // Pool the two ~1 MB scratch buffers. They are allocated up to 8 times per inference
        // batch (2 channels * 4 batch items), so pooling cuts ~16 MB of short-lived allocations
        // per ProcessChunkBatch call. FFT workspace must match Nfft exactly - see CopyStftToTensor,
        // so we reuse the per-thread _fftBuffer instead of allocating one here.
        var paddedLength = ChunkSamples + Nfft;
        var output = ArrayPool<float>.Shared.Rent(paddedLength);
        var windowSums = ArrayPool<float>.Shared.Rent(paddedLength);
        var fftBuffer = _fftBuffer.Value!;
        try
        {
            Array.Clear(output, 0, paddedLength);
            Array.Clear(windowSums, 0, paddedLength);

            for (var frame = 0; frame < ModelFrames; frame++)
            {
                Array.Clear(fftBuffer, 0, Nfft);

                for (var bin = 0; bin < ModelFrequencyBins; bin++)
                    fftBuffer[bin] = new MathNet.Numerics.Complex32(outputTensor[batchIndex, realChannelIndex, bin, frame], outputTensor[batchIndex, imaginaryChannelIndex, bin, frame]);

                for (var bin = 1; bin < FullFrequencyBins - 1; bin++)
                    fftBuffer[Nfft - bin] = fftBuffer[bin].Conjugate();

                Fourier.Inverse(fftBuffer, FourierOptions.AsymmetricScaling);

                var frameOffset = frame * HopLength;
                for (var i = 0; i < Nfft; i++)
                {
                    var weighted = fftBuffer[i].Real * _window[i];
                    output[frameOffset + i] += weighted;
                    windowSums[frameOffset + i] += _window[i] * _window[i];
                }
            }

            for (var i = 0; i < length; i++)
            {
                var sourceIndex = sourceStart + i;
                destination[destinationOffset + i] = windowSums[sourceIndex] > 1e-8
                    ? output[sourceIndex] / windowSums[sourceIndex]
                    : 0;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(output);
            ArrayPool<float>.Shared.Return(windowSums);
        }
    }

    private static float[] CreateHannWindow()
    {
        var window = new float[Nfft];
        for (var i = 0; i < Nfft; i++)
            window[i] = (float)(0.5 - (0.5 * Math.Cos((2 * Math.PI * i) / Nfft)));
        return window;
    }

    private static float[] LoadFloatWave(string filePath, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(filePath);
        var estimatedSampleCount = (int)Math.Min(
            int.MaxValue,
            Math.Ceiling(reader.TotalTime.TotalSeconds * reader.WaveFormat.SampleRate * reader.WaveFormat.Channels));
        var samples = new float[Math.Max(estimatedSampleCount, 4096)];
        var samplesWritten = 0;
        var buffer = new float[4096];
        int samplesRead;

        while ((samplesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EnsureCapacity(ref samples, samplesWritten + samplesRead);
            Array.Copy(buffer, 0, samples, samplesWritten, samplesRead);
            samplesWritten += samplesRead;
        }

        if (samplesWritten != samples.Length)
            Array.Resize(ref samples, samplesWritten);

        return samples;
    }

    private static void EnsureCapacity(ref float[] samples, int requiredLength)
    {
        if (requiredLength <= samples.Length)
            return;

        var newLength = samples.Length;
        while (newLength < requiredLength)
            newLength = (int)Math.Min(int.MaxValue, Math.Max(requiredLength, newLength * 2L));

        Array.Resize(ref samples, newLength);
    }

    private int GetDirectMlDeviceId()
    {
        if (_directMlDeviceId is int configuredDeviceId)
            return configuredDeviceId;

        var environmentValue = Environment.GetEnvironmentVariable(DirectMlDeviceIdEnvironmentVariable);
        return int.TryParse(environmentValue, out var parsedDeviceId) && parsedDeviceId >= 0
            ? parsedDeviceId
            : 0;
    }

    private static bool GetCpuMemArenaEnabled()
    {
        var environmentValue = Environment.GetEnvironmentVariable(CpuMemArenaEnvironmentVariable);
        return environmentValue is not null
            && (environmentValue.Equals("1", StringComparison.OrdinalIgnoreCase)
                || environmentValue.Equals("true", StringComparison.OrdinalIgnoreCase)
                || environmentValue.Equals("on", StringComparison.OrdinalIgnoreCase)
                || environmentValue.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static GraphOptimizationLevel GetGraphOptimizationLevel()
    {
        var environmentValue = Environment.GetEnvironmentVariable(GraphOptimizationLevelEnvironmentVariable);
        return environmentValue?.ToLowerInvariant() switch
        {
            "disable" or "disabled" => GraphOptimizationLevel.ORT_DISABLE_ALL,
            "basic" => GraphOptimizationLevel.ORT_ENABLE_BASIC,
            "extended" => GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
            _ => GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string GetModelsRoot() => Path.Join(TypeWhisperEnvironment.DataPath, "FileTranscription", "VocalIsolation");

    private sealed class StreamingMonoResampler(int sourceSampleRate, int targetSampleRate)
    {
        private readonly double _sourceFramesPerOutput = sourceSampleRate / (double)targetSampleRate;
        private double _nextOutputSourcePosition;
        private long _totalSourceFrames;
        private int _outputFramesWritten;
        private float _lastMonoSample;
        private bool _hasLastMonoSample;

        public void WriteStereoChunk(WaveFileWriter writer, float[] left, float[] right, int offset, int count)
        {
            if (count <= 0)
                return;

            var carryCount = _hasLastMonoSample ? 1 : 0;
            var sourceLength = count + carryCount;
            var resampledCapacity = (int)Math.Ceiling(sourceLength / _sourceFramesPerOutput) + 2;

            // Pool the per-call mono-mixed source and resampled scratch buffers. These are ~1 MB
            // and ~360 KB at typical 44.1 kHz -> 16 kHz ratios, called up to 4 times per inference
            // batch so pooling removes ~5 MB of short-lived allocations per batch iteration.
            var source = ArrayPool<float>.Shared.Rent(sourceLength);
            var resampled = ArrayPool<float>.Shared.Rent(resampledCapacity);
            try
            {
                if (_hasLastMonoSample)
                    source[0] = _lastMonoSample;

                for (var i = 0; i < count; i++)
                    source[carryCount + i] = (left[offset + i] + right[offset + i]) * 0.5f;

                var sourceStartFrame = _totalSourceFrames - carryCount;
                var sourceEndExclusive = _totalSourceFrames + count;
                var outputCount = 0;

                while (_nextOutputSourcePosition < sourceEndExclusive - 1)
                {
                    var localPosition = _nextOutputSourcePosition - sourceStartFrame;
                    var localIndex = (int)localPosition;
                    var fraction = localPosition - localIndex;
                    var first = source[localIndex];
                    var second = source[localIndex + 1];
                    resampled[outputCount++] = (float)(first + ((second - first) * fraction));
                    _nextOutputSourcePosition += _sourceFramesPerOutput;
                }

                if (outputCount > 0)
                {
                    writer.WriteSamples(resampled, 0, outputCount);
                    _outputFramesWritten += outputCount;
                }

                _lastMonoSample = source[carryCount + count - 1];
                _hasLastMonoSample = true;
                _totalSourceFrames += count;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(source);
                ArrayPool<float>.Shared.Return(resampled);
            }
        }

        public void Complete(WaveFileWriter writer)
        {
            if (!_hasLastMonoSample)
                return;

            var totalOutputFrames = Math.Max(1, (int)Math.Round(_totalSourceFrames * (double)targetSampleRate / sourceSampleRate));
            var remainingFrames = totalOutputFrames - _outputFramesWritten;
            if (remainingFrames <= 0)
                return;

            var trailing = new float[remainingFrames];
            Array.Fill(trailing, _lastMonoSample);
            writer.WriteSamples(trailing, 0, trailing.Length);
            _outputFramesWritten += trailing.Length;
        }
    }
}
