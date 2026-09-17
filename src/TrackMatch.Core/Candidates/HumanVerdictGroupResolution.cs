namespace TrackMatch.Core.Candidates;

/// <summary>Duplicate Groupへ投影したHuman Verdictの派生解決結果を表す。</summary>
public sealed record HumanVerdictGroupResolution(long? PreferredTrackId, HumanVerdictConflictKind ConflictKind)
{
    public bool HasConflict => ConflictKind != HumanVerdictConflictKind.None;
}

/// <summary>Group内Human Verdictから優先TrackとConflictを解決する。</summary>
public static class HumanVerdictGroupResolver
{
    public static HumanVerdictGroupResolution Resolve(IReadOnlyCollection<CandidateReview> verdicts, IReadOnlyCollection<long> groupTrackIds)
    {
        var conflictingTracks = HumanVerdictConflictDetector.FindConflictingTrackIds(verdicts);
        if (groupTrackIds.Any(conflictingTracks.Contains)) return new(null, HumanVerdictConflictKind.DuplicateVsNotDuplicate);
        var confirmed = verdicts.Where(item => item.Decision == CandidateReviewDecision.ConfirmedDuplicate).ToArray();
        var preferred = HumanVerdictPreferredTrackResolver.Resolve(confirmed, groupTrackIds);
        var distinctPreferred = confirmed.Where(item => groupTrackIds.Contains(item.Pair.TrackIdA) && groupTrackIds.Contains(item.Pair.TrackIdB)).Select(item => item.PreferredTrackId).Distinct().Count();
        if (distinctPreferred > 1) return new(null, HumanVerdictConflictKind.PreferredTrack);
        return new(preferred, HumanVerdictConflictKind.None);
    }
}
