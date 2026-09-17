namespace TrackMatch.Core.Candidates;

/// <summary>
/// Global Track Pairに対する人手の確定判定を表す。
/// </summary>
/// <remarks>
/// Human Verdictは人間の判断の正本であり、ConfirmedDuplicateではPair内の品質選好もTrack IDで保持する。
/// Library固有Keepやレビュー省略などの派生状態は保持しない。
/// </remarks>
public sealed record CandidateReview(
    CandidatePairKey Pair,
    CandidateReviewDecision Decision,
    long? PreferredTrackId,
    string? Note)
{
    /// <summary>
    /// Preferred Trackを持たない既存の呼び出しからHuman Verdictを生成する。
    /// </summary>
    /// <remarks>
    /// NotDuplicateは従来どおり生成できる。ConfirmedDuplicateはValidateで拒否し、
    /// 新仕様で必須となった優劣関係を暗黙に補完しない。
    /// </remarks>
    public CandidateReview(CandidatePairKey pair, CandidateReviewDecision decision, string? note)
        : this(pair, decision, null, note)
    {
    }

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

            return;
        }

        if (PreferredTrackId is not null)
        {
            throw new InvalidOperationException("NotDuplicateにPreferredTrackIdは指定できません。");
        }
    }
}
