using System.Buffers.Binary;
using TypeWhisper.Core.Audio;

namespace TypeWhisper.Core.Tests.Audio;

public class PcmSampleConverterTests
{
    [Fact]
    public void ConvertPcm16LeToFloat_DecodesKnownValues()
    {
        Span<byte> source = stackalloc byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(source, 0);
        BinaryPrimitives.WriteInt16LittleEndian(source[2..], short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(source[4..], short.MinValue);
        BinaryPrimitives.WriteInt16LittleEndian(source[6..], -16384);

        var destination = new float[4];
        PcmSampleConverter.ConvertPcm16LeToFloat(source, destination);

        Assert.Equal(0f, destination[0]);
        Assert.Equal(short.MaxValue / 32768f, destination[1], precision: 6);
        Assert.Equal(-1f, destination[2]);
        Assert.Equal(-0.5f, destination[3], precision: 6);
    }

    [Fact]
    public void ConvertPcm16LeToFloat_MatchesScalarForLongSequences()
    {
        var rng = new Random(17);
        var sampleCount = 4096 + 7; // ensure we exercise the scalar tail after the vector loop
        var source = new byte[sampleCount * 2];
        var expected = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var raw = (short)rng.Next(short.MinValue, short.MaxValue + 1);
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(i * 2), raw);
            expected[i] = raw / 32768f;
        }

        var actual = new float[sampleCount];
        PcmSampleConverter.ConvertPcm16LeToFloat(source, actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ConvertPcm16LeToFloat_ThrowsOnOddSourceLength()
    {
        var source = new byte[5];
        var destination = new float[3];

        Assert.Throws<ArgumentException>(() => PcmSampleConverter.ConvertPcm16LeToFloat(source, destination));
    }

    [Fact]
    public void ConvertPcm16LeToFloat_ThrowsWhenDestinationTooSmall()
    {
        var source = new byte[8];
        var destination = new float[3];

        Assert.Throws<ArgumentException>(() => PcmSampleConverter.ConvertPcm16LeToFloat(source, destination));
    }

    [Fact]
    public void ConvertFloatToPcm16Le_EncodesKnownValues()
    {
        var source = new[] { 0f, 1f, -1f, 0.5f, -0.5f };
        var destination = new byte[source.Length * 2];

        PcmSampleConverter.ConvertFloatToPcm16Le(source, destination);

        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(destination));
        Assert.Equal(32767, BinaryPrimitives.ReadInt16LittleEndian(destination.AsSpan(2)));
        Assert.Equal(-32767, BinaryPrimitives.ReadInt16LittleEndian(destination.AsSpan(4)));
        Assert.Equal((short)(0.5f * 32767), BinaryPrimitives.ReadInt16LittleEndian(destination.AsSpan(6)));
        Assert.Equal((short)(-0.5f * 32767), BinaryPrimitives.ReadInt16LittleEndian(destination.AsSpan(8)));
    }

    [Fact]
    public void ConvertFloatToPcm16Le_ClampsOutOfRangeValues()
    {
        var source = new[] { 2.5f, -2.5f };
        var destination = new byte[source.Length * 2];

        PcmSampleConverter.ConvertFloatToPcm16Le(source, destination);

        Assert.Equal(32767, BinaryPrimitives.ReadInt16LittleEndian(destination));
        Assert.Equal(-32767, BinaryPrimitives.ReadInt16LittleEndian(destination.AsSpan(2)));
    }

    [Fact]
    public void ConvertFloatToPcm16Le_MatchesScalarForLongSequences()
    {
        var rng = new Random(31);
        var sampleCount = 8192 + 5;
        var source = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            source[i] = ((float)rng.NextDouble() * 2.4f) - 1.2f; // include out-of-range values

        var expected = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var clamped = Math.Clamp(source[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(expected.AsSpan(i * 2), (short)(clamped * 32767f));
        }

        var actual = new byte[sampleCount * 2];
        PcmSampleConverter.ConvertFloatToPcm16Le(source, actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ConvertFloatToPcm16Le_ThrowsWhenDestinationTooSmall()
    {
        var source = new[] { 0f, 1f };
        var destination = new byte[3];

        Assert.Throws<ArgumentException>(() => PcmSampleConverter.ConvertFloatToPcm16Le(source, destination));
    }

    [Fact]
    public void RoundTrip_FloatToPcm16ToFloat_PreservesValuesWithinQuantizationError()
    {
        var rng = new Random(53);
        var sampleCount = 1024;
        var source = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            source[i] = ((float)rng.NextDouble() * 2f) - 1f;

        var pcm = new byte[sampleCount * 2];
        var decoded = new float[sampleCount];
        PcmSampleConverter.ConvertFloatToPcm16Le(source, pcm);
        PcmSampleConverter.ConvertPcm16LeToFloat(pcm, decoded);

        // The encoder scales by 32767 (with truncation) while the decoder scales by 32768, so the
        // worst-case round-trip error is approximately (|x| + 1) / 32768.
        for (var i = 0; i < sampleCount; i++)
        {
            var tolerance = (Math.Abs(source[i]) + 1f) / 32768f + 1e-6f;
            Assert.InRange(Math.Abs(decoded[i] - source[i]), 0f, tolerance);
        }
    }
}
