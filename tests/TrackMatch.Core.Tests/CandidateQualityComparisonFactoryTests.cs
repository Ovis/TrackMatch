using TrackMatch.Core.Candidates;
using TrackMatch.Core.Quality;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// Candidate相対品質比較の符号規約と「主に音量差」判定を検証する。
/// </summary>
public sealed class CandidateQualityComparisonFactoryTests
{
    [Fact]
    public void CreateAnalyzed_UsesBMinusAForRelativeValues()
    {
        var result = CandidateQualityComparisonFactory.CreateAnalyzed(
            CreateCandidate(),
            matchedLoudnessA: -12,
            matchedLoudnessB: -9,
            gainDifferencesDb: [3.0, 3.1, 2.9],
            CreateAnalysis(1, plr: 10, lra: 5, highFrequencyRatio: 0.02),
            CreateAnalysis(2, plr: 11, lra: 6, highFrequencyRatio: 0.03),
            DateTime.UnixEpoch);

        Assert.NotNull(result.MatchedLoudnessDifferenceLu);
        Assert.NotNull(result.GainDifferenceMeanDb);
        Assert.NotNull(result.PeakToLoudnessRatioDifferenceDb);
        Assert.NotNull(result.LoudnessRangeDifferenceLu);
        Assert.NotNull(result.RelativeHighFrequencyDifference);
        Assert.Equal(3, result.MatchedLoudnessDifferenceLu.Value, 6);
        Assert.Equal(3, result.GainDifferenceMeanDb.Value, 6);
        Assert.Equal(1, result.PeakToLoudnessRatioDifferenceDb.Value, 6);
        Assert.Equal(1, result.LoudnessRangeDifferenceLu.Value, 6);
        Assert.Equal(0.01, result.RelativeHighFrequencyDifference.Value, 6);
    }

    [Fact]
    public void CreateAnalyzed_MarksStableGainDifferenceAsPrimarilyGainDifference()
    {
        var result = CandidateQualityComparisonFactory.CreateAnalyzed(
            CreateCandidate(),
            -12,
            -9,
            [3.0, 3.1, 2.9, 3.0],
            CreateAnalysis(1, plr: 10, lra: 5, highFrequencyRatio: 0.02),
            CreateAnalysis(2, plr: 10.5, lra: 5.4, highFrequencyRatio: 0.02),
            DateTime.UnixEpoch);

        Assert.True(result.IsPrimarilyGainDifference);
    }

    [Fact]
    public void CreateAnalyzed_DoesNotMarkVariableGainAsPrimarilyGainDifference()
    {
        var result = CandidateQualityComparisonFactory.CreateAnalyzed(
            CreateCandidate(),
            -12,
            -9,
            [1.0, 2.5, 4.0, 5.5],
            CreateAnalysis(1, plr: 10, lra: 5, highFrequencyRatio: 0.02),
            CreateAnalysis(2, plr: 10.2, lra: 5.2, highFrequencyRatio: 0.02),
            DateTime.UnixEpoch);

        Assert.False(result.IsPrimarilyGainDifference);
    }

    [Fact]
    public void CreateAnalyzed_LeavesGainJudgementUnknownWhenEvidenceIsInsufficient()
    {
        var result = CandidateQualityComparisonFactory.CreateAnalyzed(
            CreateCandidate(),
            -12,
            -11,
            [1.0, 1.0],
            CreateAnalysis(1, plr: null, lra: 5, highFrequencyRatio: null),
            CreateAnalysis(2, plr: null, lra: 5, highFrequencyRatio: null),
            DateTime.UnixEpoch);

        Assert.Null(result.IsPrimarilyGainDifference);
    }

    private static CandidateComparison CreateCandidate()
        => new(
            1,
            2,
            0.99,
            0,
            TimeSpan.Zero,
            100,
            TimeSpan.FromSeconds(30),
            1,
            1,
            1);

    private static TrackQualityAnalysis CreateAnalysis(
        long trackId,
        double? plr,
        double? lra,
        double? highFrequencyRatio)
        => new(
            trackId,
            QualityAnalysisVersions.TrackQualityAnalysis,
            QualityAnalysisStatus.Analyzed,
            -12,
            -1,
            lra,
            plr,
            0,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            18000,
            false,
            null,
            highFrequencyRatio,
            null,
            DateTime.UnixEpoch,
            null);
}
