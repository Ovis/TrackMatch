using TrackMatch.Core.Classification;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 候補ペアへ校正済みしきい値を適用した分類結果を表す。
/// </summary>
public sealed record CandidateClassification(
    long TrackIdA,
    long TrackIdB,
    AudioRelationshipKind Kind,
    string Reason,
    string ThresholdProfileJson);
