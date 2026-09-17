namespace TrackMatch.Core.Candidates;

/// <summary>Candidate PairのHuman Verdict観点の状態を表す。</summary>
public enum HumanVerdictPairState
{
    Unreviewed,
    NotDuplicate,
    ConfirmedDuplicate,
    Conflict,
}

/// <summary>PairのCurrent Verdictと全体Conflictから表示状態を解決する。</summary>
public static class HumanVerdictPairStateResolver
{
    public static HumanVerdictPairState Resolve(CandidateReview? verdict, bool hasConflict)
    {
        if (hasConflict) return HumanVerdictPairState.Conflict;
        return verdict?.Decision switch
        {
            CandidateReviewDecision.NotDuplicate => HumanVerdictPairState.NotDuplicate,
            CandidateReviewDecision.ConfirmedDuplicate => HumanVerdictPairState.ConfirmedDuplicate,
            null => HumanVerdictPairState.Unreviewed,
            _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
        };
    }
}
