using TrackMatch.Core.Quality;

namespace TrackMatch.App.Quality;

/// <summary>
/// 構造化された品質解析結果を、専門値を残しつつ初心者にも判断しやすい日本語表示へ変換する。
/// </summary>
public static class CandidateQualityPresentationFormatter
{
    /// <summary>
    /// Track単体解析とCandidate比較結果から一覧・詳細表示用モデルを生成する。
    /// </summary>
    public static CandidateQualityPresentationModel Format(
        TrackQualityAnalysis? analysisA,
        TrackQualityAnalysis? analysisB,
        CandidateQualityComparison? comparison)
    {
        if (IsAnalyzing(analysisA) || IsAnalyzing(analysisB) || IsAnalyzing(comparison))
        {
            return CandidateQualityPresentationModel.Pending with
            {
                ListSummary = "音質: 解析中",
                StatusText = "音質解析: 解析中",
                SummaryLine1 = "音質を解析しています。候補レビューはそのまま利用できます。",
            };
        }

        if (comparison is not { Status: QualityAnalysisStatus.Analyzed })
        {
            var failure = FirstFailure(analysisA, analysisB, comparison);
            if (failure is not null)
            {
                return CandidateQualityPresentationModel.Pending with
                {
                    ListSummary = "音質: 解析できませんでした",
                    StatusText = "音質解析: 解析できませんでした",
                    SummaryLine1 = failure,
                };
            }

            return CandidateQualityPresentationModel.Pending;
        }

        var assessment = CandidateQualityAssessor.Assess(analysisA, analysisB, comparison);
        var loudness = FormatLoudnessSummary(assessment);
        var warning = FormatWarningSummary(assessment);
        var listSummary = warning is null ? loudness : $"{loudness} / {warning}";
        var findings = assessment.Findings
            .Select(FormatFinding)
            .ToArray();

        return new CandidateQualityPresentationModel(
            listSummary,
            "音質解析: 完了",
            FormatLoudnessSentence(assessment),
            FormatDynamicsSentence(comparison),
            FormatOverallSentence(assessment),
            findings,
            BuildMeasurements(analysisA, analysisB),
            true);
    }

    private static bool IsAnalyzing(TrackQualityAnalysis? value)
        => value?.Status == QualityAnalysisStatus.Analyzing;

    private static bool IsAnalyzing(CandidateQualityComparison? value)
        => value?.Status == QualityAnalysisStatus.Analyzing;

    private static string? FirstFailure(
        TrackQualityAnalysis? analysisA,
        TrackQualityAnalysis? analysisB,
        CandidateQualityComparison? comparison)
    {
        if (analysisA is { Status: QualityAnalysisStatus.Failed or QualityAnalysisStatus.Unsupported })
        {
            return $"音源Aを解析できませんでした。{FormatReason(analysisA.FailureReason)}";
        }

        if (analysisB is { Status: QualityAnalysisStatus.Failed or QualityAnalysisStatus.Unsupported })
        {
            return $"音源Bを解析できませんでした。{FormatReason(analysisB.FailureReason)}";
        }

        if (comparison is { Status: QualityAnalysisStatus.Failed })
        {
            return $"A/Bの音質比較を完了できませんでした。{FormatReason(comparison.FailureReason)}";
        }

        return null;
    }

    private static string FormatReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? string.Empty : $" 理由: {reason}";

    private static string FormatLoudnessSummary(CandidateQualityAssessment assessment)
        => assessment.LoudnessRelation switch
        {
            CandidateLoudnessRelation.NearlySame => "音量差: ほぼ同じ",
            CandidateLoudnessRelation.AIsLouder => $"Aが +{assessment.AbsoluteLoudnessDifferenceLu:0.0} LU",
            CandidateLoudnessRelation.BIsLouder => $"Bが +{assessment.AbsoluteLoudnessDifferenceLu:0.0} LU",
            _ => "音量差: 測定なし",
        };

    private static string FormatLoudnessSentence(CandidateQualityAssessment assessment)
        => assessment.LoudnessRelation switch
        {
            CandidateLoudnessRelation.NearlySame => "A/Bの聴感上の音量はほぼ同じです。",
            CandidateLoudnessRelation.AIsLouder => $"Aの方が{assessment.AbsoluteLoudnessDifferenceLu:0.0} LU大きく聞こえます。",
            CandidateLoudnessRelation.BIsLouder => $"Bの方が{assessment.AbsoluteLoudnessDifferenceLu:0.0} LU大きく聞こえます。",
            _ => "一致区間の聴感上の音量差を測定できませんでした。",
        };

    private static string FormatDynamicsSentence(CandidateQualityComparison comparison)
        => comparison.IsPrimarilyGainDifference switch
        {
            true => "主に音量差と考えられます。",
            false => "音量以外にもダイナミクスの違いがある可能性があります。",
            null => "音量差の性質を判断するには測定情報が不足しています。",
        };

    private static string FormatOverallSentence(CandidateQualityAssessment assessment)
    {
        if (assessment.WarningCount == 0)
        {
            return "今回測定した品質指標では、追加の注意点は確認されませんでした。";
        }

        return assessment.MaximumWarningSeverity == QualityFindingSeverity.StrongCaution
            ? $"品質面で強く確認したい項目が{assessment.WarningCount}件あります。"
            : $"品質面で確認したい項目が{assessment.WarningCount}件あります。";
    }

    private static string? FormatWarningSummary(CandidateQualityAssessment assessment)
    {
        if (assessment.WarningCount == 0)
        {
            return "注意なし";
        }

        return assessment.MaximumWarningSeverity == QualityFindingSeverity.StrongCaution
            ? $"強い注意 {assessment.WarningCount}件"
            : $"注意 {assessment.WarningCount}件";
    }

    private static CandidateQualityFindingViewModel FormatFinding(QualityFinding finding)
        => new(
            FormatSeverity(finding.Severity),
            finding.Code switch
            {
                QualityFindingCode.PrimarilyGainDifference => "A/Bの差は主に音量差と考えられます。",
                QualityFindingCode.AdditionalDynamicsDifferencePossible => "A/Bには音量以外にもダイナミクスの違いがある可能性があります。",
                QualityFindingCode.ClippingSuspicion => $"{FormatTarget(finding.Target)}にクリッピングの疑いがあります。",
                QualityFindingCode.NearZeroTruePeak => $"{FormatTarget(finding.Target)}はTrue Peakが0 dBTPに近い状態です。",
                QualityFindingCode.HighFrequencyCutoff => $"{FormatTarget(finding.Target)}は{FormatFrequency(finding.PrimaryValue)}付近から高域成分が急激に減少しています。",
                QualityFindingCode.ChannelImbalance => $"{FormatTarget(finding.Target)}は左右チャンネルのレベル差が大きくなっています。",
                _ => "確認したい音質上の特徴があります。",
            });

    private static string FormatSeverity(QualityFindingSeverity severity)
        => severity switch
        {
            QualityFindingSeverity.Information => "情報",
            QualityFindingSeverity.Reference => "参考",
            QualityFindingSeverity.Caution => "注意",
            QualityFindingSeverity.StrongCaution => "強い注意",
            _ => "参考",
        };

    private static string FormatTarget(QualityFindingTarget target)
        => target switch
        {
            QualityFindingTarget.TrackA => "音源A",
            QualityFindingTarget.TrackB => "音源B",
            QualityFindingTarget.BothTracks => "A/B両方",
            _ => "A/B比較",
        };

    private static string FormatFrequency(double? value)
        => value is null || !double.IsFinite(value.Value) ? "高域" : $"{value.Value / 1000d:0.#} kHz";

    private static IReadOnlyList<CandidateQualityMeasurementRowViewModel> BuildMeasurements(
        TrackQualityAnalysis? analysisA,
        TrackQualityAnalysis? analysisB)
        =>
        [
            new(
                "聴感上の音量 (Integrated Loudness)",
                FormatNumber(analysisA?.IntegratedLoudnessLufs, "LUFS"),
                FormatNumber(analysisB?.IntegratedLoudnessLufs, "LUFS"),
                "音源全体の聴感上の平均的な音量をLUFSで表します。値が0に近いほど大きく聞こえます。"),
            new(
                "ピークの余裕 (True Peak)",
                FormatNumber(analysisA?.TruePeakDbtp, "dBTP"),
                FormatNumber(analysisB?.TruePeakDbtp, "dBTP"),
                "再生時に生じ得るピークをdBTPで表します。0 dBTPに近いほど余裕が少なく、0を超えるとクリッピングのリスクがあります。"),
            new(
                "曲中の音量変化 (LRA)",
                FormatNumber(analysisA?.LoudnessRangeLu, "LU"),
                FormatNumber(analysisB?.LoudnessRangeLu, "LU"),
                "曲の中で聴感上の音量がどの程度変化するかを表します。大きいほど静かな部分と大きな部分の差が広い傾向があります。"),
            new(
                "PLR",
                FormatNumber(analysisA?.PeakToLoudnessRatioDb, "dB"),
                FormatNumber(analysisB?.PeakToLoudnessRatioDb, "dB"),
                "ピークと平均的な聴感音量の差を表します。一般に大きいほどピークの余裕や瞬発的なダイナミクスが残っています。"),
            new(
                "左右レベル差",
                FormatNumber(analysisA?.LeftRightLevelDifferenceDb, "dB"),
                FormatNumber(analysisB?.LeftRightLevelDifferenceDb, "dB"),
                "左右チャンネルの平均レベル差です。0 dBに近いほど左右の音量バランスが近いことを示します。"),
            new(
                "推定有効上限周波数",
                FormatFrequencyValue(analysisA?.EffectiveUpperFrequencyHz),
                FormatFrequencyValue(analysisB?.EffectiveUpperFrequencyHz),
                "有意な高域成分が存在すると推定された上限周波数です。極端に低い場合は、帯域制限や非可逆圧縮由来の高域カットを示すことがあります。"),
            new(
                "クリッピング疑いの総時間",
                FormatDuration(analysisA?.ClippingTotalDuration),
                FormatDuration(analysisB?.ClippingTotalDuration),
                "波形が上限付近へ張り付いているなど、クリッピングが疑われる区間の合計時間です。"),
            new(
                "クリッピング疑いの最長連続",
                FormatDuration(analysisA?.ClippingLongestDuration),
                FormatDuration(analysisB?.ClippingLongestDuration),
                "クリッピングが疑われる状態が連続した最長時間です。長いほど聴感上の歪みにつながる可能性があります。"),
        ];

    private static string FormatNumber(double? value, string unit)
        => value is null || !double.IsFinite(value.Value) ? "-" : $"{value.Value:0.0} {unit}";

    private static string FormatFrequencyValue(double? value)
        => value is null || !double.IsFinite(value.Value) ? "-" : $"{value.Value / 1000d:0.0} kHz";

    private static string FormatDuration(TimeSpan? value)
        => value is null ? "-" : $"{value.Value.TotalMilliseconds:0.0} ms";
}
