namespace TrackMatch.Core.Candidates;

/// <summary>
/// 候補ペアに対する人手レビュー結果を表す。
/// </summary>
public sealed record CandidateReview(
    CandidatePairKey Pair,
    CandidateReviewDecision Decision,
    string? Note,
    long? KeepTrackId = null)
{
    public void Validate()
    {
        if (Decision == CandidateReviewDecision.NotDuplicate)
        {
            if (KeepTrackId is not null)
            {
                throw new ArgumentException("NotDuplicateではKeepTrackIdを指定できない。", nameof(KeepTrackId));
            }

            return;
        }

        if (KeepTrackId is null)
        {
            throw new ArgumentException("ConfirmedDuplicateではKeepTrackIdが必要である。", nameof(KeepTrackId));
        }

        if (KeepTrackId != Pair.TrackIdA && KeepTrackId != Pair.TrackIdB)
        {
            throw new ArgumentException("KeepTrackIdは候補ペアを構成するTrackのいずれかである必要がある。", nameof(KeepTrackId));
        }
    }
}
