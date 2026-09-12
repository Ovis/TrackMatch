using TrackMatch.App.Quality;
using TrackMatch.Core.Quality;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// 音質比較の初心者向け要約と技術値表示を検証する。
/// </summary>
public sealed class CandidateQualityPresentationFormatterTests
{
    [Fact]
    public void Format_ShowsLouderSideAndWarningCount()
    {
        var result = CandidateQualityPresentationFormatter.Format(
            CreateTrack(1, clippingTotalMs: 0),
            CreateTrack(2, clippingTotalMs: 150),
            CreateComparison(loudnessDifference: 3.2, primarilyGain: false));

        Assert.Contains("Bが +3.2 LU", result.ListSummary, StringComparison.Ordinal);
        Assert.Contains("強い注意 1件", result.ListSummary, StringComparison.Ordinal);
        Assert.Contains("Bの方が3.2 LU大きく", result.SummaryLine1, StringComparison.Ordinal);
        Assert.Contains(result.Findings, item => item.Severity == "強い注意");
    }

    [Fact]
    public void Format_ShowsNearlySameWithoutWarning()
    {
        var result = CandidateQualityPresentationFormatter.Format(
            CreateTrack(1),
            CreateTrack(2),
            CreateComparison(loudnessDifference: 0.2, primarilyGain: true));

        Assert.Equal("音量差: ほぼ同じ / 注意なし", result.ListSummary);
        Assert.Contains("ほぼ同じ", result.SummaryLine1, StringComparison.Ordinal);
        Assert.Contains("主に音量差", result.SummaryLine2, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_KeepsTechnicalMeasurementsAvailable()
    {
        var result = CandidateQualityPresentationFormatter.Format(
            CreateTrack(1),
            CreateTrack(2),
            CreateComparison(loudnessDifference: 1.0, primarilyGain: null));

        Assert.Contains(result.Measurements, item => item.Label.Contains("Integrated Loudness", StringComparison.Ordinal));
        Assert.Contains(result.Measurements, item => item.Label.Contains("True Peak", StringComparison.Ordinal));
        Assert.Contains(result.Measurements, item => item.Label == "PLR");
    }

    private static TrackQualityAnalysis CreateTrack(long trackId, double clippingTotalMs = 0)
        => new(
            trackId,
            QualityAnalysisVersions.TrackQualityAnalysis,
            QualityAnalysisStatus.Analyzed,
            -12,
            -1,
            5,
            11,
            clippingTotalMs > 0 ? 100 : 0,
            clippingTotalMs > 0 ? 2 : 0,
            TimeSpan.FromMilliseconds(clippingTotalMs),
            TimeSpan.FromMilliseconds(clippingTotalMs > 0 ? 25 : 0),
            0.2,
            19000,
            false,
            null,
            0.02,
            0.9,
            DateTime.UnixEpoch,
            null);

    private static CandidateQualityComparison CreateComparison(double loudnessDifference, bool? primarilyGain)
        => new(
            1,
            2,
            QualityAnalysisVersions.CandidateQualityComparison,
            QualityAnalysisStatus.Analyzed,
            loudnessDifference,
            loudnessDifference,
            0.1,
            0.2,
            0.2,
            primarilyGain,
            0,
            DateTime.UnixEpoch,
            null);
}
