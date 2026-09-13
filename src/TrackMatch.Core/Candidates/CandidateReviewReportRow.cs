using TrackMatch.Core.Classification;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 分類前の詳細比較結果と人手レビュー状態を含め、GUIで確認する候補1行を表す。
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
    IReadOnlyList<string> GenresB,
    TimeSpan DurationA,
    TimeSpan DurationB,
    long FileSizeA,
    long FileSizeB,
    string? FormatA,
    string? FormatB,
    string? CodecA,
    string? CodecB,
    int? BitrateKbpsA,
    int? BitrateKbpsB,
    int? SampleRateHzA,
    int? SampleRateHzB,
    int? BitDepthA,
    int? BitDepthB,
    int? ChannelsA,
    int? ChannelsB,
    CandidateReviewDecision? ReviewDecision = null,
    long? KeepTrackId = null,
    uint? YearA = null,
    uint? YearB = null);
