namespace TrackMatch.Core.Probe;

/// <summary>
/// 期待関係ごとに集計したProbe測定値の分布を表す。
/// </summary>
public sealed record ProbeRelationStatistics(
    string ExpectedRelation,
    int Count,
    ProbeMetricStatistics Similarity,
    ProbeMetricStatistics MinimumCoverage,
    ProbeMetricStatistics MaximumCoverage,
    ProbeMetricStatistics DurationRatio);
