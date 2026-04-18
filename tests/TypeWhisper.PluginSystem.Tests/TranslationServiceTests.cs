using System.Reflection;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class TranslationServiceTests
{
    private delegate int FindBestLogitIndexDelegate(ReadOnlySpan<float> logits);

    private static readonly FindBestLogitIndexDelegate FindBestLogitIndexMethod = typeof(TranslationService)
        .GetMethod("FindBestLogitIndex", BindingFlags.NonPublic | BindingFlags.Static)
        ?.CreateDelegate<FindBestLogitIndexDelegate>()
        ?? throw new InvalidOperationException("Expected TranslationService.FindBestLogitIndex to exist.");

    [Fact]
    public void FindBestLogitIndex_PicksFirstMatchingMaximum()
    {
        var logits = new[] { 1f, 3f, 3f, 2f };

        var bestIndex = FindBestLogitIndex(logits);

        Assert.Equal(1, bestIndex);
    }

    [Fact]
    public void FindBestLogitIndex_IgnoresNaNsLikeScalarLoop()
    {
        var logits = new[] { float.NaN, -5f, float.NaN, -2f, -2f };

        var bestIndex = FindBestLogitIndex(logits);

        Assert.Equal(3, bestIndex);
    }

    [Fact]
    public void FindBestLogitIndex_MatchesScalarLoopAcrossRandomInputs()
    {
        var random = new Random(1234);
        for (var trial = 0; trial < 2000; trial++)
        {
            var logits = new float[random.Next(1, 1024)];
            for (var i = 0; i < logits.Length; i++)
            {
                var mode = random.Next(0, 40);
                logits[i] = mode switch
                {
                    0 => float.NaN,
                    1 => float.NegativeInfinity,
                    2 => float.PositiveInfinity,
                    _ => (float)((random.NextDouble() * 2000d) - 1000d),
                };
            }

            Assert.Equal(FindBestLogitIndexScalar(logits), FindBestLogitIndex(logits));
        }
    }

    private static int FindBestLogitIndex(float[] logits)
        => FindBestLogitIndexMethod(logits);

    private static int FindBestLogitIndexScalar(float[] logits)
    {
        var bestIndex = 0;
        var bestValue = float.NegativeInfinity;
        for (var i = 0; i < logits.Length; i++)
        {
            var value = logits[i];
            if (value > bestValue)
            {
                bestValue = value;
                bestIndex = i;
            }
        }

        return bestIndex;
    }
}
