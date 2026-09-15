namespace TrackMatch.Core.Candidates;

/// <summary>
/// Global Track Pairに対する人手の確定判定を表す。
/// </summary>
/// <remarks>
/// どのTrackを残すかはLibrary固有のDuplicate Group状態であり、このGlobal Verdictには保持しない。
/// </remarks>
public sealed record CandidateReview(
    CandidatePairKey Pair,
    CandidateReviewDecision Decision,
    string? Note)
{
    /// <summary>
    /// 永続化前にGlobal Verdictとして有効な値であることを検証する。
    /// </summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Decision))
        {
            throw new ArgumentOutOfRangeException(nameof(Decision));
        }
    }
}
