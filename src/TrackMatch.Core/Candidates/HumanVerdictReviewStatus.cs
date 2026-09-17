namespace TrackMatch.Core.Candidates;

/// <summary>Candidate一覧でHuman Verdictを表示するレビュー状態を表す。</summary>
public sealed record HumanVerdictReviewStatus(
    HumanVerdictPairState PairState,
    HumanVerdictReevaluationState ReevaluationState,
    HumanVerdictConflictKind ConflictKind)
{
    public bool IsReviewed => PairState is not HumanVerdictPairState.Unreviewed;
    public bool IsReReviewRecommended => IsReviewed && ReevaluationState == HumanVerdictReevaluationState.Recommended;
    public bool HasConflict => ConflictKind != HumanVerdictConflictKind.None || PairState == HumanVerdictPairState.Conflict;
}
