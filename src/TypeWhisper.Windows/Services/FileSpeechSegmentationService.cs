using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using SherpaOnnx;
using TypeWhisper.Core.Audio;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Services;

public sealed class AudioSpeechSegment
{
    private float[]? _samples;
    private float[]? _sourceSamples;
    private int _sourceOffset;

    public AudioSpeechSegment(float[] samples, double startSeconds, double endSeconds)
    {
        _samples = samples;
        SampleCount = samples.Length;
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
    }

    private AudioSpeechSegment(float[] sourceSamples, int sourceOffset, int sampleCount, double startSeconds, double endSeconds)
    {
        _sourceSamples = sourceSamples;
        _sourceOffset = sourceOffset;
        SampleCount = sampleCount;
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
    }

    public static AudioSpeechSegment CreateSlice(float[] sourceSamples, int sourceOffset, int sampleCount, double startSeconds, double endSeconds) =>
        new(sourceSamples, sourceOffset, sampleCount, startSeconds, endSeconds);

    public float[] Samples
    {
        get
        {
            if (_samples is not null)
                return _samples;

            var samples = new float[SampleCount];
            Array.Copy(_sourceSamples!, _sourceOffset, samples, 0, SampleCount);
            _samples = samples;
            return samples;
        }
    }

    public int SampleCount { get; } = 0;
    public double StartSeconds { get; }
    public double EndSeconds { get; }

    public void ReleaseSamples()
    {
        _samples = [];
        _sourceSamples = null;
        _sourceOffset = 0;
    }
}

public sealed class FileSpeechSegmentationService
{
    private const int SampleRate = 16000;
    private const int VadWindowSize = 512;
    private const int MaxSegmentSamples = SampleRate * 60;
    // 64 KiB = 32768 PCM16 samples (~2s at 16 kHz mono). Larger reads amortize per-call
    // MediaFoundationResampler overhead and match the AudioFileService buffer size.
    private const int ReadBufferBytes = 65536;

    private readonly AudioFileService _audioFile;

    public FileSpeechSegmentationService(AudioFileService audioFile)
    {
        _audioFile = audioFile;
    }

    public Task<IReadOnlyList<AudioSpeechSegment>> SegmentAsync(float[] samples, CancellationToken cancellationToken = default) =>
        Task.Run(() => Segment(samples, cancellationToken), cancellationToken);

    public async IAsyncEnumerable<AudioSpeechSegment> StreamSegmentsAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException(Loc.Instance["Error.FileNotFound"], filePath);

        var modelPath = Path.Combine(AppContext.BaseDirectory, "Resources", "silero_vad.onnx");
        if (!File.Exists(modelPath))
        {
            Debug.WriteLine($"[FileSpeechSegmentation] Missing VAD model at {modelPath}; falling back to full audio.");
            var samples = await _audioFile.LoadAudioAsync(filePath, SampleRate, 1, cancellationToken);
            yield return new AudioSpeechSegment(samples, 0, samples.Length / (double)SampleRate);
            yield break;
        }

        var config = CreateConfig(modelPath);

        using var reader = new MediaFoundationReader(filePath);
        using var resampled = new MediaFoundationResampler(reader, new WaveFormat(SampleRate, 16, 1))
        {
            ResamplerQuality = 60
        };
        using var vad = new VoiceActivityDetector(config, 60);

        var byteBuffer = new byte[ReadBufferBytes];
        var sampleBuffer = new float[ReadBufferBytes / 2];
        var window = new float[VadWindowSize];
        var drainedSegments = new List<AudioSpeechSegment>();

        int bytesRead;
        while ((bytesRead = resampled.Read(byteBuffer, 0, byteBuffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var samplesRead = bytesRead / 2;
            PcmSampleConverter.ConvertPcm16LeToFloat(
                byteBuffer.AsSpan(0, samplesRead * 2),
                sampleBuffer.AsSpan(0, samplesRead));

            for (var offset = 0; offset < samplesRead; offset += VadWindowSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var windowLength = Math.Min(VadWindowSize, samplesRead - offset);
                Array.Copy(sampleBuffer, offset, window, 0, windowLength);
                if (windowLength < window.Length)
                    Array.Clear(window, windowLength, window.Length - windowLength);

                vad.AcceptWaveform(window);

                DrainSegments(vad, drainedSegments, cancellationToken);
                foreach (var segment in drainedSegments)
                    yield return segment;

                drainedSegments.Clear();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        vad.Flush();
        DrainSegments(vad, drainedSegments, cancellationToken);
        foreach (var segment in drainedSegments)
            yield return segment;
    }

    private static IReadOnlyList<AudioSpeechSegment> Segment(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
            return [];

        var modelPath = Path.Combine(AppContext.BaseDirectory, "Resources", "silero_vad.onnx");
        if (!File.Exists(modelPath))
        {
            Debug.WriteLine($"[FileSpeechSegmentation] Missing VAD model at {modelPath}; falling back to full audio.");
            return [new AudioSpeechSegment(samples, 0, samples.Length / (double)SampleRate)];
        }

        var config = CreateConfig(modelPath);

        using var vad = new VoiceActivityDetector(config, 60);
        var segments = new List<AudioSpeechSegment>();
        var window = new float[VadWindowSize];

        var numWindows = (samples.Length + VadWindowSize - 1) / VadWindowSize;
        for (var i = 0; i < numWindows; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var windowOffset = i * VadWindowSize;
            var windowLength = Math.Min(VadWindowSize, samples.Length - windowOffset);
            Array.Copy(samples, windowOffset, window, 0, windowLength);
            if (windowLength < window.Length)
                Array.Clear(window, windowLength, window.Length - windowLength);

            vad.AcceptWaveform(window);
            DrainSegments(vad, segments, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        vad.Flush();
        DrainSegments(vad, segments, cancellationToken);

        var speechSegments = segments.Count > 0
            ? segments
            : [new AudioSpeechSegment(samples, 0, samples.Length / (double)SampleRate)];

        return SplitLongSegments(speechSegments);
    }

    private static VadModelConfig CreateConfig(string modelPath)
    {
        return new VadModelConfig
        {
            SileroVad = new SileroVadModelConfig
            {
                Model = modelPath,
                Threshold = 0.5f,
                MinSilenceDuration = 0.5f,
                MinSpeechDuration = 0.25f,
                WindowSize = VadWindowSize,
            },
            SampleRate = SampleRate,
            Debug = 0,
        };
    }

    private static void DrainSegments(VoiceActivityDetector vad, List<AudioSpeechSegment> segments, CancellationToken cancellationToken)
    {
        while (!vad.IsEmpty())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var segment = vad.Front();
            vad.Pop();

            if (segment.Samples.Length < 1600)
                continue;

            var start = segment.Start / (double)SampleRate;
            var end = start + segment.Samples.Length / (double)SampleRate;
            var speechSegment = new AudioSpeechSegment(segment.Samples, start, end);
            if (speechSegment.Samples.Length <= MaxSegmentSamples)
            {
                segments.Add(speechSegment);
                continue;
            }

            segments.AddRange(SplitLongSegment(speechSegment));
        }
    }

    // Keep engine requests bounded when VAD yields one very long uninterrupted speech span.
    private static IReadOnlyList<AudioSpeechSegment> SplitLongSegments(IReadOnlyList<AudioSpeechSegment> segments)
    {
        var splitSegments = new List<AudioSpeechSegment>(segments.Count);

        foreach (var segment in segments)
        {
            splitSegments.AddRange(SplitLongSegment(segment));
        }

        return splitSegments;
    }

    private static IReadOnlyList<AudioSpeechSegment> SplitLongSegment(AudioSpeechSegment segment)
    {
        if (segment.SampleCount <= MaxSegmentSamples)
            return [segment];

        var sourceSamples = segment.Samples;
        var splitCount = (segment.SampleCount + MaxSegmentSamples - 1) / MaxSegmentSamples;
        var splitSegments = new List<AudioSpeechSegment>(splitCount);
        for (var offset = 0; offset < segment.SampleCount; offset += MaxSegmentSamples)
        {
            var chunkLength = Math.Min(MaxSegmentSamples, segment.SampleCount - offset);

            var chunkStart = segment.StartSeconds + offset / (double)SampleRate;
            var chunkEnd = Math.Min(segment.EndSeconds, chunkStart + chunkLength / (double)SampleRate);
            splitSegments.Add(AudioSpeechSegment.CreateSlice(sourceSamples, offset, chunkLength, chunkStart, chunkEnd));
        }

        return splitSegments;
    }
}
