using System.IO;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using TypeWhisper.Core.Audio;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Services;

public sealed class AudioFileService
{
    private const int TargetSampleRate = 16000;
    private const int TargetChannels = 1;
    private const int ReadBufferBytes = 4096;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".wma",
        ".mp4", ".mkv", ".avi", ".mov", ".webm"
    };

    public static bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return SupportedExtensions.Contains(ext);
    }

    public async Task<float[]> LoadAudioAsync(string filePath, CancellationToken cancellationToken = default)
        => await LoadAudioAsync(filePath, TargetSampleRate, TargetChannels, cancellationToken);

    public async Task<float[]> LoadAudioAsync(
        string filePath,
        int targetSampleRate,
        int targetChannels,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException(Loc.Instance["Error.FileNotFound"], filePath);

        return await Task.Run(() => LoadAudio(filePath, targetSampleRate, targetChannels, cancellationToken), cancellationToken);
    }

    public async IAsyncEnumerable<AudioSpeechSegment> StreamAudioChunksAsync(
        string filePath,
        int chunkFrameCount,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in StreamAudioChunksAsync(filePath, TargetSampleRate, TargetChannels, chunkFrameCount, cancellationToken))
            yield return chunk;
    }

    public async IAsyncEnumerable<AudioSpeechSegment> StreamAudioChunksAsync(
        string filePath,
        int targetSampleRate,
        int targetChannels,
        int chunkFrameCount,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException(Loc.Instance["Error.FileNotFound"], filePath);
        if (chunkFrameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkFrameCount));

        using var reader = new MediaFoundationReader(filePath);
        using var resampled = new MediaFoundationResampler(reader, new WaveFormat(targetSampleRate, 16, targetChannels))
        {
            ResamplerQuality = 60
        };

        var chunkSamples = chunkFrameCount * targetChannels;
        var chunkBuffer = new float[chunkSamples];
        var byteBuffer = new byte[ReadBufferBytes];
        var bufferedSamples = 0;
        long emittedFrames = 0;
        int bytesRead;

        while ((bytesRead = resampled.Read(byteBuffer, 0, byteBuffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sampleCount = bytesRead / 2;
            var sampleOffset = 0;
            while (sampleOffset < sampleCount)
            {
                var capacityRemaining = chunkSamples - bufferedSamples;
                var toCopy = Math.Min(capacityRemaining, sampleCount - sampleOffset);

                PcmSampleConverter.ConvertPcm16LeToFloat(
                    byteBuffer.AsSpan(sampleOffset * 2, toCopy * 2),
                    chunkBuffer.AsSpan(bufferedSamples, toCopy));

                bufferedSamples += toCopy;
                sampleOffset += toCopy;

                if (bufferedSamples != chunkSamples)
                    continue;

                var start = emittedFrames / (double)targetSampleRate;
                emittedFrames += chunkFrameCount;
                var end = emittedFrames / (double)targetSampleRate;
                var segmentSamples = new float[chunkSamples];
                Array.Copy(chunkBuffer, 0, segmentSamples, 0, chunkSamples);
                yield return new AudioSpeechSegment(segmentSamples, start, end);
                bufferedSamples = 0;
            }
        }

        if (bufferedSamples > 0)
        {
            var remainingFrames = bufferedSamples / targetChannels;
            var start = emittedFrames / (double)targetSampleRate;
            var end = start + (remainingFrames / (double)targetSampleRate);
            var segmentSamples = new float[bufferedSamples];
            Array.Copy(chunkBuffer, 0, segmentSamples, 0, bufferedSamples);
            yield return new AudioSpeechSegment(segmentSamples, start, end);
        }
    }

    public static float[] ConvertAudio(
        float[] samples,
        int sourceSampleRate,
        int sourceChannels,
        int targetSampleRate,
        int targetChannels)
    {
        if (sourceChannels <= 0 || targetChannels <= 0)
            throw new ArgumentOutOfRangeException(sourceChannels <= 0 ? nameof(sourceChannels) : nameof(targetChannels));
        if (samples.Length == 0)
            return [];

        var sourceFrames = samples.Length / sourceChannels;
        var targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * (double)targetSampleRate / sourceSampleRate));
        var converted = new float[targetFrames * targetChannels];

        for (var frame = 0; frame < targetFrames; frame++)
        {
            var sourcePosition = frame * (double)sourceSampleRate / targetSampleRate;
            var sourceIndex = Math.Min((int)sourcePosition, sourceFrames - 1);
            var nextIndex = Math.Min(sourceIndex + 1, sourceFrames - 1);
            var fraction = sourcePosition - sourceIndex;

            for (var channel = 0; channel < targetChannels; channel++)
            {
                var first = ReadConvertedChannel(samples, sourceChannels, targetChannels, sourceIndex, channel);
                var second = ReadConvertedChannel(samples, sourceChannels, targetChannels, nextIndex, channel);
                converted[frame * targetChannels + channel] = (float)(first + (second - first) * fraction);
            }
        }

        return converted;
    }

    private static float[] LoadAudio(string filePath, int targetSampleRate, int targetChannels, CancellationToken cancellationToken)
    {
        using var reader = new MediaFoundationReader(filePath);
        using var resampled = new MediaFoundationResampler(reader,
            new WaveFormat(targetSampleRate, 16, targetChannels))
        {
            ResamplerQuality = 60
        };

        var estimatedSampleCount = (int)Math.Min(
            int.MaxValue,
            Math.Ceiling(reader.TotalTime.TotalSeconds * targetSampleRate * targetChannels));
        var samples = new float[Math.Max(estimatedSampleCount, ReadBufferBytes / 2)];
        var samplesWritten = 0;
        var buffer = new byte[ReadBufferBytes];
        int bytesRead;

        while ((bytesRead = resampled.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sampleCount = bytesRead / 2; // 16-bit = 2 bytes per sample
            EnsureCapacity(ref samples, samplesWritten + sampleCount);

            PcmSampleConverter.ConvertPcm16LeToFloat(
                buffer.AsSpan(0, sampleCount * 2),
                samples.AsSpan(samplesWritten, sampleCount));

            samplesWritten += sampleCount;
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

    private static float ReadConvertedChannel(float[] samples, int sourceChannels, int targetChannels, int frameIndex, int targetChannel)
    {
        if (targetChannels == sourceChannels)
            return samples[frameIndex * sourceChannels + Math.Min(targetChannel, sourceChannels - 1)];
        if (targetChannels > sourceChannels)
            return samples[frameIndex * sourceChannels + Math.Min(targetChannel, sourceChannels - 1)];

        var sum = 0f;
        for (var i = 0; i < sourceChannels; i++)
            sum += samples[frameIndex * sourceChannels + i];

        return sum / sourceChannels;
    }

    public static TimeSpan GetDuration(string filePath)
    {
        using var reader = new MediaFoundationReader(filePath);
        return reader.TotalTime;
    }
}
