using TrackMatch.Core.Classification;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 人手確認・エクスポート用の候補分類行を表す。
/// </summary>
public sealed record CandidateClassificationReportRow(
    long TrackIdA,
    long TrackIdB,
    AudioRelationshipKind Kind,
    string Reason,
    double Similarity,
    double CoverageA,
    double CoverageB,
    double DurationRatio,
    TimeSpan BestOffset,
    TimeSpan MatchedDuration,
    string PathA,
    string PathB,
    IReadOnlyList<string> ArtistsA,
    IReadOnlyList<string> ArtistsB,
    string? TitleA,
    string? TitleB,
    string? AlbumA,
    string? AlbumB,
    IReadOnlyList<string> GenresA,
    IReadOnlyList<string> GenresB);
