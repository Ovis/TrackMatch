namespace TrackMatch.Core.Candidates;

/// <summary>
/// 候補ペア詳細比較1回分の集計結果を表す。
/// </summary>
public sealed record CandidateAnalysisResult(
    int TotalCandidates,
    int ComparedCandidates,
    int ReusedCandidates,
    int SkippedCandidates);
