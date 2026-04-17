using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Core.Audio;

/// <summary>
/// High-throughput conversions between little-endian 16-bit signed PCM and single-precision
/// floating-point samples in the range [-1.0, 1.0]. Used on the hot paths of file decoding
/// and WAV encoding so the per-sample cost is minimized on modern SIMD-capable CPUs.
/// </summary>
public static class PcmSampleConverter
{
    private const float Int16ToFloatScale = 1f / 32768f;
    private const float FloatToInt16Scale = 32767f;

    /// <summary>
    /// Converts a little-endian PCM16 byte span into normalized floats in [-1.0, 1.0).
    /// <paramref name="source"/> length must be a multiple of two bytes;
    /// <paramref name="destination"/> must have at least <c>source.Length / 2</c> elements.
    /// </summary>
    public static void ConvertPcm16LeToFloat(ReadOnlySpan<byte> source, Span<float> destination)
    {
        if ((source.Length & 1) != 0)
            throw new ArgumentException("Source length must be an even number of bytes (16-bit samples).", nameof(source));

        var sampleCount = source.Length >> 1;
        if (destination.Length < sampleCount)
            throw new ArgumentException("Destination span is smaller than the number of samples to write.", nameof(destination));

        if (BitConverter.IsLittleEndian)
        {
            // On little-endian hosts the PCM byte layout matches the memory layout of short,
            // so we can reinterpret without copying and then vectorize widen + scale.
            ConvertInt16ToFloat(MemoryMarshal.Cast<byte, short>(source), destination);
            return;
        }

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]);
            destination[i] = sample * Int16ToFloatScale;
        }
    }

    /// <summary>
    /// Converts normalized float samples into little-endian PCM16 bytes. Values outside
    /// [-1.0, 1.0] are clamped. <paramref name="destination"/> must have capacity for
    /// <c>source.Length * 2</c> bytes.
    /// </summary>
    public static void ConvertFloatToPcm16Le(ReadOnlySpan<float> source, Span<byte> destination)
    {
        var requiredBytes = source.Length * 2;
        if (destination.Length < requiredBytes)
            throw new ArgumentException("Destination span is smaller than the encoded output size.", nameof(destination));

        if (BitConverter.IsLittleEndian)
        {
            ConvertFloatToInt16(source, MemoryMarshal.Cast<byte, short>(destination[..requiredBytes]));
            return;
        }

        for (var i = 0; i < source.Length; i++)
        {
            var clamped = Math.Clamp(source[i], -1f, 1f);
            var pcm = (short)(clamped * FloatToInt16Scale);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], pcm);
        }
    }

    private static void ConvertInt16ToFloat(ReadOnlySpan<short> source, Span<float> destination)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var shortLane = Vector<short>.Count;
            var intLane = Vector<int>.Count;
            var scale = new Vector<float>(Int16ToFloatScale);

            for (; i + shortLane <= source.Length; i += shortLane)
            {
                var shortVec = new Vector<short>(source.Slice(i, shortLane));
                Vector.Widen(shortVec, out Vector<int> lowInt, out Vector<int> highInt);

                var lowFloat = Vector.ConvertToSingle(lowInt) * scale;
                var highFloat = Vector.ConvertToSingle(highInt) * scale;

                lowFloat.CopyTo(destination[i..]);
                highFloat.CopyTo(destination[(i + intLane)..]);
            }
        }

        for (; i < source.Length; i++)
            destination[i] = source[i] * Int16ToFloatScale;
    }

    private static void ConvertFloatToInt16(ReadOnlySpan<float> source, Span<short> destination)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var intLane = Vector<int>.Count;
            var shortLane = Vector<short>.Count;
            var scale = new Vector<float>(FloatToInt16Scale);
            var minClamp = new Vector<float>(-1f);
            var maxClamp = new Vector<float>(1f);

            // Each iteration consumes 2 * Vector<int>.Count floats (equal to Vector<short>.Count)
            // and produces one Vector<short> worth of samples.
            for (; i + shortLane <= source.Length; i += shortLane)
            {
                var lowFloat = new Vector<float>(source.Slice(i, intLane));
                var highFloat = new Vector<float>(source.Slice(i + intLane, intLane));

                lowFloat = Vector.Min(Vector.Max(lowFloat, minClamp), maxClamp) * scale;
                highFloat = Vector.Min(Vector.Max(highFloat, minClamp), maxClamp) * scale;

                var lowInt = Vector.ConvertToInt32(lowFloat);
                var highInt = Vector.ConvertToInt32(highFloat);
                var narrow = Vector.Narrow(lowInt, highInt);
                narrow.CopyTo(destination[i..]);
            }
        }

        for (; i < source.Length; i++)
        {
            var clamped = Math.Clamp(source[i], -1f, 1f);
            destination[i] = (short)(clamped * FloatToInt16Scale);
        }
    }
}
