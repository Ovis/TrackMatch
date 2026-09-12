namespace TrackMatch.Core.Quality;

/// <summary>
/// Track単体の音質解析結果を保持する。
/// </summary>
public sealed record TrackQualityAnalysis(
    long TrackId,
    int AnalysisVersion,
    QualityAnalysisStatus Status,
    double? IntegratedLoudnessLufs,
    double? TruePeakDbtp,
    double? LoudnessRangeLu,
    double? PeakToLoudnessRatioDb,
    long PeakNearSampleCount,
    long ClippingRunCount,
    TimeSpan ClippingTotalDuration,
    TimeSpan ClippingLongestDuration,
    double? LeftRightLevelDifferenceDb,
    double? EffectiveUpperFrequencyHz,
    bool? HasHighFrequencyCutoff,
    double? HighFrequencyCutoffHz,
    double? HighFrequencyEnergyRatio,
    double? HighFrequencyConsistency,
    DateTime? AnalyzedAtUtc,
    string? FailureReason);
