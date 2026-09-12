namespace TrackMatch.Core.Quality;

/// <summary>
/// Track単体解析とCandidate相対比較から、品質の優劣を断定せず確認ポイントだけを抽出する。
/// </summary>
public static class CandidateQualityAssessor
{
    private const double NearlySameLoudnessThresholdLu = 0.5;
    private const double NearZeroTruePeakThresholdDbtp = -0.1;
    private const double ChannelImbalanceReferenceThresholdDb = 1.5;
    private const double ChannelImbalanceCautionThresholdDb = 6.0;
    private static readonly TimeSpan ClippingCautionLongest = TimeSpan.FromMilliseconds(3);
    private static readonly TimeSpan ClippingCautionTotal = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ClippingStrongLongest = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ClippingStrongTotal = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A/Bの解析結果を、一覧や詳細画面で利用できる構造化評価へ変換する。
    /// </summary>
    public static CandidateQualityAssessment Assess(
        TrackQualityAnalysis? analysisA,
        TrackQualityAnalysis? analysisB,
        CandidateQualityComparison? comparison)
    {
        var findings = new List<QualityFinding>();
        var loudnessRelation = CandidateLoudnessRelation.Unknown;
        double? absoluteLoudnessDifference = null;

        if (comparison is { Status: QualityAnalysisStatus.Analyzed, MatchedLoudnessDifferenceLu: not null })
        {
            var difference = comparison.MatchedLoudnessDifferenceLu.Value;
            absoluteLoudnessDifference = Math.Abs(difference);
            loudnessRelation = Math.Abs(difference) < NearlySameLoudnessThresholdLu
                ? CandidateLoudnessRelation.NearlySame
                : difference > 0
                    ? CandidateLoudnessRelation.BIsLouder
                    : CandidateLoudnessRelation.AIsLouder;

            if (comparison.IsPrimarilyGainDifference == true)
            {
                findings.Add(new QualityFinding(
                    QualityFindingCode.PrimarilyGainDifference,
                    QualityFindingSeverity.Information,
                    QualityFindingTarget.Comparison,
                    comparison.GainDifferenceMeanDb,
                    comparison.GainDifferenceStandardDeviationDb));
            }
            else if (comparison.IsPrimarilyGainDifference == false)
            {
                findings.Add(new QualityFinding(
                    QualityFindingCode.AdditionalDynamicsDifferencePossible,
                    QualityFindingSeverity.Reference,
                    QualityFindingTarget.Comparison,
                    comparison.GainDifferenceMeanDb,
                    comparison.GainDifferenceStandardDeviationDb));
            }
        }

        AddTrackFindings(findings, analysisA, QualityFindingTarget.TrackA);
        AddTrackFindings(findings, analysisB, QualityFindingTarget.TrackB);

        return new CandidateQualityAssessment(
            loudnessRelation,
            absoluteLoudnessDifference,
            findings);
    }

    private static void AddTrackFindings(
        ICollection<QualityFinding> findings,
        TrackQualityAnalysis? analysis,
        QualityFindingTarget target)
    {
        if (analysis is not { Status: QualityAnalysisStatus.Analyzed })
        {
            return;
        }

        var clippingSeverity = GetClippingSeverity(analysis);
        if (clippingSeverity is not null)
        {
            findings.Add(new QualityFinding(
                QualityFindingCode.ClippingSuspicion,
                clippingSeverity.Value,
                target,
                analysis.ClippingTotalDuration.TotalMilliseconds,
                analysis.ClippingLongestDuration.TotalMilliseconds));
        }

        if (analysis.TruePeakDbtp is >= NearZeroTruePeakThresholdDbtp)
        {
            // True Peakが0 dBTP近傍でも、それだけでクリッピングとは断定できないためReferenceに留める。
            findings.Add(new QualityFinding(
                QualityFindingCode.NearZeroTruePeak,
                QualityFindingSeverity.Reference,
                target,
                analysis.TruePeakDbtp));
        }

        if (analysis.HasHighFrequencyCutoff == true)
        {
            // 高域減衰は元ソースやマスタリングでも起こり得るため、単独では品質劣化扱いにしない。
            findings.Add(new QualityFinding(
                QualityFindingCode.HighFrequencyCutoff,
                QualityFindingSeverity.Reference,
                target,
                analysis.HighFrequencyCutoffHz,
                analysis.HighFrequencyEnergyRatio));
        }

        if (analysis.LeftRightLevelDifferenceDb is { } channelDifference
            && double.IsFinite(channelDifference))
        {
            var absolute = Math.Abs(channelDifference);
            if (absolute >= ChannelImbalanceCautionThresholdDb)
            {
                findings.Add(new QualityFinding(
                    QualityFindingCode.ChannelImbalance,
                    QualityFindingSeverity.Caution,
                    target,
                    channelDifference));
            }
            else if (absolute >= ChannelImbalanceReferenceThresholdDb)
            {
                findings.Add(new QualityFinding(
                    QualityFindingCode.ChannelImbalance,
                    QualityFindingSeverity.Reference,
                    target,
                    channelDifference));
            }
        }
    }

    private static QualityFindingSeverity? GetClippingSeverity(TrackQualityAnalysis analysis)
    {
        if (analysis.ClippingRunCount <= 0)
        {
            return null;
        }

        if (analysis.ClippingLongestDuration >= ClippingStrongLongest
            || analysis.ClippingTotalDuration >= ClippingStrongTotal)
        {
            return QualityFindingSeverity.StrongCaution;
        }

        if (analysis.ClippingLongestDuration >= ClippingCautionLongest
            || analysis.ClippingTotalDuration >= ClippingCautionTotal)
        {
            return QualityFindingSeverity.Caution;
        }

        return QualityFindingSeverity.Reference;
    }
}
