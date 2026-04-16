using System.IO;
using NAudio.Wave;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AudioFileServiceTests
{
    [Fact]
    public async Task StreamAudioChunksAsync_SplitsMonoWaveIntoExpectedChunkSizes()
    {
        var path = CreateTestWave(sampleRate: 16000, samples: CreateRamp(150000));

        try
        {
            var sut = new AudioFileService();
            var chunkLengths = new List<int>();
            var starts = new List<double>();
            var ends = new List<double>();

            await foreach (var chunk in sut.StreamAudioChunksAsync(path, 40000, CancellationToken.None))
            {
                chunkLengths.Add(chunk.Samples.Length);
                starts.Add(chunk.StartSeconds);
                ends.Add(chunk.EndSeconds);
            }

            Assert.Equal([40000, 40000, 40000, 30000], chunkLengths);
            Assert.Equal([0d, 2.5d, 5d, 7.5d], starts);
            Assert.Equal([2.5d, 5d, 7.5d, 9.375d], ends);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StreamAudioChunksAsync_PreservesTotalSamplesAgainstFullLoad()
    {
        var samples = CreateRamp(96000);
        var path = CreateTestWave(sampleRate: 16000, samples);

        try
        {
            var sut = new AudioFileService();
            var loaded = await sut.LoadAudioAsync(path, CancellationToken.None);
            var streamed = new List<float>();

            await foreach (var chunk in sut.StreamAudioChunksAsync(path, 32000, CancellationToken.None))
                streamed.AddRange(chunk.Samples);

            Assert.Equal(loaded.Length, streamed.Count);
            Assert.True(loaded.SequenceEqual(streamed));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTestWave(int sampleRate, float[] samples)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static float[] CreateRamp(int length)
    {
        var samples = new float[length];
        for (var i = 0; i < length; i++)
            samples[i] = ((i % 200) - 100) / 100f;

        return samples;
    }
}
