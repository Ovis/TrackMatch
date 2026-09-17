namespace TrackMatch.Core.Candidates;

/// <summary>GroupのHuman VerdictからPreferred Track状態を解決する。</summary>
public static class HumanVerdictPreferredTrackStatusResolver
{
    public static HumanVerdictPreferredTrackResult Resolve(IReadOnlyCollection<CandidateReview> verdicts, IReadOnlyCollection<long> groupTrackIds)
    {
        var group = HumanVerdictGroupResolver.Resolve(verdicts, groupTrackIds);
        if (group.HasConflict) return new(HumanVerdictPreferredTrackStatus.Conflict, null);
        return group.PreferredTrackId is { } trackId
            ? new(HumanVerdictPreferredTrackStatus.Resolved, trackId)
            : new(HumanVerdictPreferredTrackStatus.Unavailable, null);
    }
}
