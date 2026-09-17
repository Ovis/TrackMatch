namespace TrackMatch.Core.Candidates;

/// <summary>アプリ起動中の1段Undoで復元する直前のHuman Verdictを保持する。</summary>
public sealed record HumanVerdictUndoEntry(CandidatePairKey Pair, CandidateReview? PreviousVerdict)
{
    /// <summary>Undo対象Pairと保持Verdictの整合を検証する。</summary>
    public void Validate()
    {
        if (PreviousVerdict is not null && PreviousVerdict.Pair != Pair)
        {
            throw new InvalidOperationException("Undo対象Pairと復元Human Verdictが一致していません。");
        }
        PreviousVerdict?.Validate();
    }
}
