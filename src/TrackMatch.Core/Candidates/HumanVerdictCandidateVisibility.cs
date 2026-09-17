namespace TrackMatch.Core.Candidates;

/// <summary>Candidate一覧のタブ分類をHuman Verdict状態から解決する。</summary>
public static class HumanVerdictCandidateVisibility
{
    public static bool IsReviewed(CandidateReview? verdict) => verdict is not null;
    public static bool IsUnreviewed(CandidateReview? verdict) => verdict is null;
    public static bool IsReReviewRecommended(CandidateReview? verdict, HumanVerdictReevaluationState state)
        => verdict is not null && state == HumanVerdictReevaluationState.Recommended;
}
