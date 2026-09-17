namespace TrackMatch.Core.Candidates;

/// <summary>
/// Candidate Review操作から正規化済みHuman Verdictを生成する。
/// </summary>
public static class CandidateReviewFactory
{
    /// <summary>重複ではないという人手判定を生成する。</summary>
    public static CandidateReview NotDuplicate(long trackIdA, long trackIdB, string? note = null)
        => new(CandidatePairKey.Create(trackIdA, trackIdB), CandidateReviewDecision.NotDuplicate, null, note);

    /// <summary>同一音源かつ優先Trackを伴う人手判定を生成する。</summary>
    public static CandidateReview ConfirmedDuplicate(long trackIdA, long trackIdB, long preferredTrackId, string? note = null)
    {
        var review = new CandidateReview(
            CandidatePairKey.Create(trackIdA, trackIdB),
            CandidateReviewDecision.ConfirmedDuplicate,
            preferredTrackId,
            note);
        review.Validate();
        return review;
    }
}
