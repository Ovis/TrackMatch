namespace TrackMatch.Core.Candidates;

/// <summary>
/// 候補Trackペアをraw Fingerprintで詳細比較した測定値を表す。
/// </summary>
public sealed record CandidateComparison(
    long TrackIdA,
    long TrackIdB,
    double Similarity,
    int BestOffsetItems,
    TimeSpan BestOffset,
    int MatchedItems,
    TimeSpan MatchedDuration,
    double CoverageA,
    double CoverageB,
    double DurationRatio);
