using TrackMatch.Core.Classification;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 分類前の詳細比較結果も含め、GUIで人手確認する候補1行を表す。
/// </summary>
public sealed record CandidateReviewReportRow(
    long TrackIdA,
    long TrackIdB,
    AudioRelationshipKind? Kind,
    string? Reason,
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
