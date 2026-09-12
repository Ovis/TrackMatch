namespace TrackMatch.Core.Quality;

/// <summary>
/// Candidate A/Bの聴感上の音量関係を表す。
/// </summary>
public enum CandidateLoudnessRelation
{
    Unknown,
    NearlySame,
    AIsLouder,
    BIsLouder,
}

/// <summary>
/// Candidate品質比較をUIで要約するための構造化評価結果を保持する。
/// </summary>
public sealed record CandidateQualityAssessment(
    CandidateLoudnessRelation LoudnessRelation,
    double? AbsoluteLoudnessDifferenceLu,
    IReadOnlyList<QualityFinding> Findings)
{
    /// <summary>
    /// UI上で警告として件数表示する所見数を取得する。
    /// InformationとReferenceは警告件数へ含めない。
    /// </summary>
    public int WarningCount => Findings.Count(item => item.Severity is QualityFindingSeverity.Caution or QualityFindingSeverity.StrongCaution);

    /// <summary>
    /// 警告対象所見の最大重要度を取得する。警告がなければnullを返す。
    /// </summary>
    public QualityFindingSeverity? MaximumWarningSeverity
        => Findings
            .Where(item => item.Severity is QualityFindingSeverity.Caution or QualityFindingSeverity.StrongCaution)
            .Select(item => (QualityFindingSeverity?)item.Severity)
            .DefaultIfEmpty(null)
            .Max();
}
