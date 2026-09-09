namespace TrackMatch.Core.Probe;

/// <summary>
/// Probe結果CSVから読み込んだ、しきい値校正に使用する測定値を表す。
/// </summary>
public sealed record ProbeMeasurement(
    string ExpectedRelation,
    double Similarity,
    double CoverageA,
    double CoverageB,
    double DurationRatio)
{
    public double MinimumCoverage => Math.Min(CoverageA, CoverageB);

    public double MaximumCoverage => Math.Max(CoverageA, CoverageB);
}
