namespace TrackMatch.Core.Candidates;

/// <summary>
/// Candidate Generation 1回分の計算結果を表す。
/// </summary>
public sealed record CandidateGenerationResult(
    int TrackCount,
    int SegmentCount,
    IReadOnlyList<CandidatePair> Pairs,
    int IndexedTrackCount = 0,
    bool IsFullRebuild = false);
