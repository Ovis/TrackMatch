using TrackMatch.Application;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// 品質解析世代トークンが高速な連続発行でも衝突しないことを検証する。
/// </summary>
public sealed class QualityAnalysisGenerationTests
{
    [Fact]
    public void CreateTimestamp_RapidCallsAreStrictlyIncreasing()
    {
        var timestamps = Enumerable.Range(0, 1000)
            .Select(_ => QualityAnalysisGeneration.CreateTimestamp())
            .ToArray();

        Assert.All(timestamps, timestamp => Assert.Equal(DateTimeKind.Utc, timestamp.Kind));
        for (var index = 1; index < timestamps.Length; index++)
        {
            Assert.True(timestamps[index] > timestamps[index - 1]);
        }
    }
}
