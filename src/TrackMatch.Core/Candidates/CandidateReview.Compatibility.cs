namespace TrackMatch.Core.Candidates;

public sealed partial record CandidateReview
{
    /// <summary>
    /// NotDuplicateを生成する既存呼び出し元との移行互換用コンストラクタ。
    /// </summary>
    /// <remarks>
    /// ConfirmedDuplicateはPreferredTrackIdを失うため、この形式では生成できない。
    /// </remarks>
    public CandidateReview(CandidatePairKey pair, CandidateReviewDecision decision, string? note)
        : this(pair, decision, null, note)
    {
        if (decision == CandidateReviewDecision.ConfirmedDuplicate)
        {
            throw new InvalidOperationException("ConfirmedDuplicateにはPreferredTrackIdが必要です。");
        }
    }
}
