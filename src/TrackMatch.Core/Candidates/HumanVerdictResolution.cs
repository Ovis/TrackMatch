namespace TrackMatch.Core.Candidates;

/// <summary>Human Verdict集合から派生表示・処置へ渡す解決結果を表す。</summary>
public sealed record HumanVerdictResolution(
    IReadOnlyList<CandidateReview> Verdicts,
    IReadOnlySet<long> ConflictingTrackIds)
{
    /// <summary>人手判定同士に論理矛盾が存在するかを返す。</summary>
    public bool HasConflict => ConflictingTrackIds.Count > 0;
}

/// <summary>Current Human Verdictを正本として派生状態を解決する。</summary>
public static class HumanVerdictResolver
{
    /// <summary>Verdictを変更せず保持したまま矛盾情報を付加する。</summary>
    public static HumanVerdictResolution Resolve(IReadOnlyCollection<CandidateReview> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        foreach (var verdict in verdicts) verdict.Validate();
        return new HumanVerdictResolution(verdicts.ToArray(), HumanVerdictConflictDetector.FindConflictingTrackIds(verdicts));
    }
}
