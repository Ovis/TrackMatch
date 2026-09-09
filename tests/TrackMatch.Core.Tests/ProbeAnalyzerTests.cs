using TrackMatch.Core.Probe;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class ProbeAnalyzerTests
{
    [Fact]
    public void Analyze_GroupsRelationsAndCalculatesMedian()
    {
        var measurements = new[]
        {
            new ProbeMeasurement("duplicate", 0.90, 0.95, 0.96, 0.99),
            new ProbeMeasurement("duplicate", 0.98, 0.97, 0.99, 1.00),
            new ProbeMeasurement("duplicate", 0.94, 0.96, 0.98, 0.98),
            new ProbeMeasurement("tv-size", 0.92, 0.35, 0.99, 0.36),
        };

        var result = new ProbeAnalyzer().Analyze(measurements);

        Assert.Equal(2, result.Count);
        var duplicate = Assert.Single(result, item => item.ExpectedRelation == "duplicate");
        Assert.Equal(3, duplicate.Count);
        Assert.Equal(0.94, duplicate.Similarity.Median, 6);
        Assert.Equal(0.96, duplicate.MinimumCoverage.Median, 6);
        Assert.Equal(0.98, duplicate.MaximumCoverage.Median, 6);
        Assert.Equal(0.99, duplicate.DurationRatio.Median, 6);

        var tvSize = Assert.Single(result, item => item.ExpectedRelation == "tv-size");
        Assert.Equal(0.35, tvSize.MinimumCoverage.Median, 6);
        Assert.Equal(0.99, tvSize.MaximumCoverage.Median, 6);
    }

    [Fact]
    public void Analyze_EvenCountUsesAverageOfMiddleValues()
    {
        var measurements = new[]
        {
            new ProbeMeasurement("duplicate", 0.80, 0.90, 0.90, 0.95),
            new ProbeMeasurement("duplicate", 1.00, 1.00, 1.00, 1.00),
        };

        var result = Assert.Single(new ProbeAnalyzer().Analyze(measurements));

        Assert.Equal(0.90, result.Similarity.Median, 6);
    }
}
