namespace TrackMatch.Core.Candidates;

/// <summary>ConfirmedDuplicate Verdict群のPreferred TrackからGroupの派生優先状態を解決する。</summary>
public static class HumanVerdictPreferredTrackResolver
{
    /// <summary>一意なPreferred Trackが存在する場合だけ返し、競合時はnullを返す。</summary>
    public static long? Resolve(IReadOnlyCollection<CandidateReview> verdicts, IReadOnlyCollection<long> groupTrackIds)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        ArgumentNullException.ThrowIfNull(groupTrackIds);
        var group = groupTrackIds.ToHashSet();
        var preferred = verdicts
            .Where(item => item.Decision == CandidateReviewDecision.ConfirmedDuplicate
                && group.Contains(item.Pair.TrackIdA)
                && group.Contains(item.Pair.TrackIdB))
            .Select(item => item.PreferredTrackId)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .Distinct()
            .ToArray();
        return preferred.Length == 1 ? preferred[0] : null;
    }
}
