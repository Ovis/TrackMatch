namespace TrackMatch.Core.Quality;

/// <summary>
/// Candidate固有の音質比較結果を保持する。
/// </summary>
public sealed record CandidateQualityComparison(
    long TrackIdA,
    long TrackIdB,
    int ComparisonVersion,
    QualityAnalysisStatus Status,
    double? MatchedLoudnessDifferenceLu,
    double? GainDifferenceMeanDb,
    double? GainDifferenceStandardDeviationDb,
    double? PeakToLoudnessRatioDifferenceDb,
    double? LoudnessRangeDifferenceLu,
    bool? IsPrimarilyGainDifference,
    double? RelativeHighFrequencyDifference,
    DateTime? ComparedAtUtc,
    string? FailureReason);
