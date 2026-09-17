namespace TrackMatch.Core.Candidates;

/// <summary>Candidateレビュー操作をHuman Verdictへ変換する。</summary>
public static class HumanVerdictReviewActionResolver
{
    /// <summary>Clear以外の操作をHuman Verdictへ変換する。Clearはnullを返す。</summary>
    public static CandidateReview? Resolve(CandidatePairKey pair, HumanVerdictReviewAction action)
        => action switch
        {
            HumanVerdictReviewAction.MarkNotDuplicate => CandidateReviewFactory.NotDuplicate(pair.TrackIdA, pair.TrackIdB),
            HumanVerdictReviewAction.ConfirmDuplicatePreferA => CandidateReviewFactory.ConfirmedDuplicate(pair.TrackIdA, pair.TrackIdB, pair.TrackIdA),
            HumanVerdictReviewAction.ConfirmDuplicatePreferB => CandidateReviewFactory.ConfirmedDuplicate(pair.TrackIdA, pair.TrackIdB, pair.TrackIdB),
            HumanVerdictReviewAction.Clear => null,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
}
