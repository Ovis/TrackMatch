namespace TrackMatch.Core.Candidates;

public sealed partial record CandidateReview
{
    /// <summary>
    /// 旧コードからの段階移行用に、Pairと判定だけを受け取る。
    /// </summary>
    /// <remarks>
    /// ConfirmedDuplicateでは従来のA/B順序からAを暫定Preferredとする。UIと永続化の主要経路は明示Preferredを使用し、
    /// この互換経路は残存テスト・補助コードを新しい正本モデルへ移すためだけに用いる。
    /// </remarks>
    public static CandidateReview FromLegacy(CandidatePairKey pair, CandidateReviewDecision decision, string? note)
        => decision == CandidateReviewDecision.ConfirmedDuplicate
            ? new CandidateReview(pair, decision, pair.TrackIdA, note)
            : new CandidateReview(pair, decision, null, note);
}
