using TrackMatch.Core.Quality;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// Candidate品質所見の音量関係・警告件数・重要度判定を検証する。
/// </summary>
public sealed class CandidateQualityAssessorTests
{
    [Fact]
    public void Assess_TreatsSmallMatchedLoudnessDifferenceAsNearlySame()
    {
        var assessment = CandidateQualityAssessor.Assess(
            CreateTrackAnalysis(1),
            CreateTrackAnalysis(2),
            CreateComparison(loudnessDifference: 0.4));

        Assert.Equal(CandidateLoudnessRelation.NearlySame, assessment.LoudnessRelation);
        Assert.Equal(0, assessment.WarningCount);
    }

    [Fact]
    public void Assess_ReportsLouderSideFromBMinusASign()
    {
        var assessment = CandidateQualityAssessor.Assess(
            CreateTrackAnalysis(1),
            CreateTrackAnalysis(2),
            CreateComparison(loudnessDifference: 3.2));

        Assert.Equal(CandidateLoudnessRelation.BIsLouder, assessment.LoudnessRelation);
        Assert.NotNull(assessment.AbsoluteLoudnessDifferenceLu);
        Assert.Equal(3.2, assessment.AbsoluteLoudnessDifferenceLu.Value, 6);
    }

    [Fact]
    public void Assess_CountsOnlyCautionAndStrongCautionAsWarnings()
    {
        var trackA = CreateTrackAnalysis(
            1,
            truePeakDbtp: -0.05,
            clippingRuns: 1,
            clippingTotal: TimeSpan.FromMilliseconds(25),
            clippingLongest: TimeSpan.FromMilliseconds(4));
        var trackB = CreateTrackAnalysis(
            2,
            hasHighFrequencyCutoff: true,
            channelDifferenceDb: 7);

        var assessment = CandidateQualityAssessor.Assess(
            trackA,
            trackB,
            CreateComparison(loudnessDifference: 2));

        Assert.Equal(2, assessment.WarningCount);
        Assert.Equal(QualityFindingSeverity.Caution, assessment.MaximumWarningSeverity);
        Assert.Contains(assessment.Findings, item => item.Code == QualityFindingCode.NearZeroTruePeak && item.Severity == QualityFindingSeverity.Reference);
        Assert.Contains(assessment.Findings, item => item.Code == QualityFindingCode.HighFrequencyCutoff && item.Severity == QualityFindingSeverity.Reference);
    }

    [Fact]
    public void Assess_UsesStrongCautionForSustainedClippingSuspicion()
    {
        var trackB = CreateTrackAnalysis(
            2,
            clippingRuns: 3,
            clippingTotal: TimeSpan.FromMilliseconds(130),
            clippingLongest: TimeSpan.FromMilliseconds(25));

        var assessment = CandidateQualityAssessor.Assess(
            CreateTrackAnalysis(1),
            trackB,
            CreateComparison(loudnessDifference: 4));

        Assert.Equal(1, assessment.WarningCount);
        Assert.Equal(QualityFindingSeverity.StrongCaution, assessment.MaximumWarningSeverity);
        Assert.Contains(
            assessment.Findings,
            item => item.Code == QualityFindingCode.ClippingSuspicion
                && item.Target == QualityFindingTarget.TrackB
                && item.Severity == QualityFindingSeverity.StrongCaution);
    }

    [Fact]
    public void Assess_EmitsStructuredDynamicsFindingWithoutQualityVerdict()
    {
        var comparison = CreateComparison(loudnessDifference: 3) with
        {
            IsPrimarilyGainDifference = false,
            GainDifferenceMeanDb = 3,
            GainDifferenceStandardDeviationDb = 1.2,
        };

        var assessment = CandidateQualityAssessor.Assess(
            CreateTrackAnalysis(1),
            CreateTrackAnalysis(2),
            comparison);

        Assert.Contains(
            assessment.Findings,
            item => item.Code == QualityFindingCode.AdditionalDynamicsDifferencePossible
                && item.Severity == QualityFindingSeverity.Reference
                && item.Target == QualityFindingTarget.Comparison);
    }

    private static TrackQualityAnalysis CreateTrackAnalysis(
        long trackId,
        double? truePeakDbtp = -1,
        long clippingRuns = 0,
        TimeSpan? clippingTotal = null,
        TimeSpan? clippingLongest = null,
        bool? hasHighFrequencyCutoff = false,
        double? channelDifferenceDb = 0)
        => new(
            trackId,
            QualityAnalysisVersions.TrackQualityAnalysis,
            QualityAnalysisStatus.Analyzed,
            -12,
            truePeakDbtp,
            5,
            11,
            0,
            clippingRuns,
            clippingTotal ?? TimeSpan.Zero,
            clippingLongest ?? TimeSpan.Zero,
            channelDifferenceDb,
            18000,
            hasHighFrequencyCutoff,
            hasHighFrequencyCutoff == true ? 16000 : null,
            0.02,
            null,
            DateTime.UnixEpoch,
            null);

    private static CandidateQualityComparison CreateComparison(double loudnessDifference)
        => new(
            1,
            2,
            QualityAnalysisVersions.CandidateQualityComparison,
            QualityAnalysisStatus.Analyzed,
            loudnessDifference,
            loudnessDifference,
            0.1,
            0,
            0,
            true,
            0,
            DateTime.UnixEpoch,
            null);
}
