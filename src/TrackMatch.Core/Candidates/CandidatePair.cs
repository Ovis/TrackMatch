namespace TrackMatch.Core.Candidates;

/// <summary>
/// 詳細Fingerprint比較へ渡すTrackペアと、候補抽出時の最良区間距離を表す。
/// </summary>
public sealed record CandidatePair(
    long TrackIdA,
    long TrackIdB,
    int MinimumSegmentHashDistance);
