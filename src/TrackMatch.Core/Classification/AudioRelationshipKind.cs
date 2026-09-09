namespace TrackMatch.Core.Classification;

/// <summary>
/// 2音源間の音響的な関係を表す候補分類。
/// </summary>
public enum AudioRelationshipKind
{
    DuplicateCandidate,
    ShortVersionCandidate,
    AlternateVersionCandidate,
    NeedsReview,
}
