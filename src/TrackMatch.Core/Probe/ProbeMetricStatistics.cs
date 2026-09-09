namespace TrackMatch.Core.Probe;

/// <summary>
/// 1つのProbe測定指標の分布要約を表す。
/// </summary>
public sealed record ProbeMetricStatistics(
    double Minimum,
    double Median,
    double Maximum);
