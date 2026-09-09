namespace TrackMatch.Core.Classification;

/// <summary>
/// 音響関係分類の結果を表す。
/// </summary>
public sealed record RelationshipClassificationResult(
    AudioRelationshipKind Kind,
    string Reason);
