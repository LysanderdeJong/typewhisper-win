using System.Buffers;
using System.Buffers.Binary;

namespace TypeWhisper.Core.Audio;

public static class WavEncoder
{
    private const int HeaderSize = 44;
    private const int WriteChunkSamples = 4096;

    public static byte[] Encode(float[] samples, int sampleRate = 16000, int channels = 1, int bitsPerSample = 16)
    {
        var bytesPerSample = bitsPerSample / 8;
        var dataLength = samples.Length * bytesPerSample;
        var buffer = new byte[HeaderSize + dataLength];

        WriteHeader(buffer.AsSpan(0, HeaderSize), dataLength, sampleRate, channels, bitsPerSample, bytesPerSample);
        WritePcm16Samples(buffer.AsSpan(HeaderSize), samples);

        return buffer;
    }

    public static async Task WriteAsync(
        Stream stream,
        float[] samples,
        int sampleRate = 16000,
        int channels = 1,
        int bitsPerSample = 16,
        CancellationToken cancellationToken = default)
    {
        var bytesPerSample = bitsPerSample / 8;
        var dataLength = samples.Length * bytesPerSample;
        var header = new byte[HeaderSize];
        WriteHeader(header, dataLength, sampleRate, channels, bitsPerSample, bytesPerSample);
        await stream.WriteAsync(header, cancellationToken);

        var chunkBuffer = ArrayPool<byte>.Shared.Rent(WriteChunkSamples * bytesPerSample);
        try
        {
            var remainingSamples = samples.Length;
            var offset = 0;
            while (remainingSamples > 0)
            {
                var chunkSamples = Math.Min(remainingSamples, WriteChunkSamples);
                var chunkBytes = chunkSamples * bytesPerSample;
                WritePcm16Samples(chunkBuffer.AsSpan(0, chunkBytes), samples.AsSpan(offset, chunkSamples));
                await stream.WriteAsync(chunkBuffer.AsMemory(0, chunkBytes), cancellationToken);
                offset += chunkSamples;
                remainingSamples -= chunkSamples;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }
    }

    private static void WriteHeader(Span<byte> buffer, int dataLength, int sampleRate, int channels, int bitsPerSample, int bytesPerSample)
    {
        // RIFF header
        "RIFF"u8.CopyTo(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], 36 + dataLength);
        "WAVE"u8.CopyTo(buffer[8..]);

        // fmt sub-chunk
        "fmt "u8.CopyTo(buffer[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[16..], 16); // sub-chunk size
        BinaryPrimitives.WriteInt16LittleEndian(buffer[20..], 1);  // PCM format
        BinaryPrimitives.WriteInt16LittleEndian(buffer[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[28..], sampleRate * channels * bytesPerSample); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(buffer[32..], (short)(channels * bytesPerSample)); // block align
        BinaryPrimitives.WriteInt16LittleEndian(buffer[34..], (short)bitsPerSample);

        // data sub-chunk
        "data"u8.CopyTo(buffer[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[40..], dataLength);
    }

    private static void WritePcm16Samples(Span<byte> buffer, ReadOnlySpan<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            var pcm = (short)(clamped * 32767);
            BinaryPrimitives.WriteInt16LittleEndian(buffer[(i * 2)..], pcm);
        }
    }
}
