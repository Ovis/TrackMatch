using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Quality;

/// <summary>
/// Candidate一致区間の測定値とTrack単体解析結果から、永続化可能な相対品質比較を構築する。
/// </summary>
public static class CandidateQualityComparisonFactory
{
    private const double GainStandardDeviationThresholdDb = 0.75;
    private const double PlrDifferenceThresholdDb = 1.5;
    private const double LraDifferenceThresholdLu = 1.5;

    /// <summary>
    /// 一致区間で測定したA/Bラウドネスと時間窓ごとのゲイン差から解析済み比較結果を生成する。
    /// </summary>
    public static CandidateQualityComparison CreateAnalyzed(
        CandidateComparison candidate,
        double? matchedLoudnessA,
        double? matchedLoudnessB,
        IReadOnlyCollection<double> gainDifferencesDb,
        TrackQualityAnalysis analysisA,
        TrackQualityAnalysis analysisB,
        DateTime comparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(gainDifferencesDb);
        ArgumentNullException.ThrowIfNull(analysisA);
        ArgumentNullException.ThrowIfNull(analysisB);

        double? gainMean = gainDifferencesDb.Count == 0 ? null : gainDifferencesDb.Average();
        double? gainStandardDeviation = gainDifferencesDb.Count < 2
            ? null
            : CalculateStandardDeviation(gainDifferencesDb, gainMean!.Value);
        var plrDifference = Difference(analysisA.PeakToLoudnessRatioDb, analysisB.PeakToLoudnessRatioDb);
        var lraDifference = Difference(analysisA.LoudnessRangeLu, analysisB.LoudnessRangeLu);

        bool? primarilyGainDifference = null;
        if (gainDifferencesDb.Count >= 3
            && gainStandardDeviation is not null
            && plrDifference is not null
            && lraDifference is not null)
        {
            // 対応区間の音量差が時間方向に安定し、全曲のPLR/LRA差も小さい場合だけ
            // 「主に音量差」と扱う。条件を外れた場合も具体的なマスタリング処理までは断定しない。
            primarilyGainDifference = gainStandardDeviation.Value <= GainStandardDeviationThresholdDb
                && Math.Abs(plrDifference.Value) <= PlrDifferenceThresholdDb
                && Math.Abs(lraDifference.Value) <= LraDifferenceThresholdLu;
        }

        return new CandidateQualityComparison(
            candidate.TrackIdA,
            candidate.TrackIdB,
            QualityAnalysisVersions.CandidateQualityComparison,
            QualityAnalysisStatus.Analyzed,
            Difference(matchedLoudnessA, matchedLoudnessB),
            gainMean,
            gainStandardDeviation,
            plrDifference,
            lraDifference,
            primarilyGainDifference,
            Difference(analysisA.HighFrequencyEnergyRatio, analysisB.HighFrequencyEnergyRatio),
            comparedAtUtc,
            null);
    }

    private static double CalculateStandardDeviation(IReadOnlyCollection<double> values, double mean)
    {
        var variance = values.Sum(value => Math.Pow(value - mean, 2)) / values.Count;
        return Math.Sqrt(variance);
    }

    private static double? Difference(double? a, double? b)
        => a is not null && b is not null ? b.Value - a.Value : null;
}
