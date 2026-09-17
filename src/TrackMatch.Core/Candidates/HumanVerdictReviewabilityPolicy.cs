namespace TrackMatch.Core.Candidates;

/// <summary>Candidateを通常レビュー対象として表示・操作できるかをHuman Verdict基準で判定する。</summary>
public static class HumanVerdictReviewabilityPolicy
{
    /// <summary>Current Human Verdictが存在しないPairだけを未レビューとして扱う。</summary>
    public static bool IsUnreviewed(CandidateReview? verdict) => verdict is null;

    /// <summary>Conflictは既存Verdictを破棄せず、通常処置とは分離して再確認対象とする。</summary>
    public static bool RequiresConflictResolution(bool hasConflict) => hasConflict;
}
