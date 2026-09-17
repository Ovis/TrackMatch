namespace TrackMatch.Core.Candidates;

/// <summary>
/// Global Track Pairに対する人手の確定判定を表す。
/// </summary>
/// <remarks>
/// ConfirmedDuplicateでは、同一音源という判定に加えて人が残したいと判断したTrackもGlobal Verdictの一部として保持する。
/// Library固有の表示状態や派生Group状態は保持しない。
/// </remarks>
public sealed record CandidateReview(
    CandidatePairKey Pair,
    CandidateReviewDecision Decision,
    long? PreferredTrackId,
    string? Note)
{
    /// <summary>
    /// 永続化前にHuman Verdictとして有効な値であることを検証する。
    /// </summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Decision))
        {
            throw new ArgumentOutOfRangeException(nameof(Decision));
        }

        if (Decision == CandidateReviewDecision.ConfirmedDuplicate)
        {
            if (PreferredTrackId is null)
            {
                throw new InvalidOperationException("ConfirmedDuplicateにはPreferredTrackIdが必要です。");
            }

            if (PreferredTrackId != Pair.TrackIdA && PreferredTrackId != Pair.TrackIdB)
            {
                throw new InvalidOperationException("PreferredTrackIdは判定対象PairのTrackである必要があります。");
            }
        }
        else if (PreferredTrackId is not null)
        {
            throw new InvalidOperationException("NotDuplicateにPreferredTrackIdは指定できません。");
        }
    }
}
