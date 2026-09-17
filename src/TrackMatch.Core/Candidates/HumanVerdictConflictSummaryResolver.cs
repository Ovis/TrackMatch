namespace TrackMatch.Core.Candidates;

/// <summary>Group解決結果からConflict表示用Summaryを生成する。</summary>
public static class HumanVerdictConflictSummaryResolver
{
    public static HumanVerdictConflictSummary Resolve(IReadOnlyCollection<CandidateReview> verdicts, IReadOnlyCollection<long> groupTrackIds)
    {
        var resolution = HumanVerdictGroupResolver.Resolve(verdicts, groupTrackIds);
        if (!resolution.HasConflict) return new(HumanVerdictConflictKind.None, []);
        var tracks = resolution.ConflictKind == HumanVerdictConflictKind.DuplicateVsNotDuplicate
            ? HumanVerdictConflictDetector.FindConflictingTrackIds(verdicts).Where(groupTrackIds.Contains).Order().ToArray()
            : groupTrackIds.Order().ToArray();
        return new(resolution.ConflictKind, tracks);
    }
}
