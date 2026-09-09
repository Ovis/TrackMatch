namespace TrackMatch.Core.Probe;

/// <summary>
/// 既知カテゴリ付きProbe結果を集計し、判定しきい値の校正材料を生成する。
/// </summary>
public sealed class ProbeAnalyzer
{
    public IReadOnlyList<ProbeRelationStatistics> Analyze(IEnumerable<ProbeMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        return measurements
            .GroupBy(measurement => measurement.ExpectedRelation, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var values = group.ToArray();
                return new ProbeRelationStatistics(
                    group.Key,
                    values.Length,
                    Summarize(values.Select(value => value.Similarity)),
                    Summarize(values.Select(value => value.MinimumCoverage)),
                    Summarize(values.Select(value => value.MaximumCoverage)),
                    Summarize(values.Select(value => value.DurationRatio)));
            })
            .ToArray();
    }

    private static ProbeMetricStatistics Summarize(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0)
        {
            throw new InvalidOperationException("集計対象の測定値が存在しない。");
        }

        var middle = values.Length / 2;
        var median = values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2d
            : values[middle];

        return new ProbeMetricStatistics(values[0], median, values[^1]);
    }
}
