namespace TrackMatch.Core.Comparison;

/// <summary>
/// 2つのraw fingerprintを最良Offsetで整列した比較結果を表す。
/// </summary>
public sealed record FingerprintComparisonResult(
    double Similarity,
    int BestOffsetItems,
    TimeSpan BestOffset,
    int MatchedItems,
    TimeSpan MatchedDuration,
    double CoverageA,
    double CoverageB);
