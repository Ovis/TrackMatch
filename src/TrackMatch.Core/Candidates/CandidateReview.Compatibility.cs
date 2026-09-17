namespace TrackMatch.Core.Candidates;

public sealed partial record CandidateReview
{
    /// <summary>既存呼び出し元をHuman Verdictモデルへ段階移行する互換コンストラクタ。</summary>
    /// <remarks>
    /// ConfirmedDuplicateの旧呼び出しではPair Aを暫定Preferredとする。新規コードはPreferredTrackIdを明示する4引数形式か
    /// CandidateReviewFactoryを使用する。
    /// </remarks>
    public CandidateReview(CandidatePairKey pair, CandidateReviewDecision decision, string? note)
        : this(pair, decision, decision == CandidateReviewDecision.ConfirmedDuplicate ? pair.TrackIdA : null, note)
    {
    }
}
