namespace TrackMatch.Core.Classification;

/// <summary>
/// 音響関係分類で使用するしきい値を表す。
/// 実ライブラリのProbe結果から校正する前提とし、Coreでは既定値を持たない。
/// </summary>
public sealed record RelationshipThresholdProfile(
    double DuplicateMinimumSimilarity,
    double DuplicateMinimumCoverage,
    double DuplicateMinimumDurationRatio,
    double ShortVersionMinimumSimilarity,
    double ShortVersionMinimumMaximumCoverage,
    double ShortVersionMaximumMinimumCoverage,
    double ShortVersionMaximumDurationRatio,
    double AlternateVersionMinimumSimilarity)
{
    public void Validate()
    {
        ValidateUnitInterval(DuplicateMinimumSimilarity, nameof(DuplicateMinimumSimilarity));
        ValidateUnitInterval(DuplicateMinimumCoverage, nameof(DuplicateMinimumCoverage));
        ValidateUnitInterval(DuplicateMinimumDurationRatio, nameof(DuplicateMinimumDurationRatio));
        ValidateUnitInterval(ShortVersionMinimumSimilarity, nameof(ShortVersionMinimumSimilarity));
        ValidateUnitInterval(ShortVersionMinimumMaximumCoverage, nameof(ShortVersionMinimumMaximumCoverage));
        ValidateUnitInterval(ShortVersionMaximumMinimumCoverage, nameof(ShortVersionMaximumMinimumCoverage));
        ValidateUnitInterval(ShortVersionMaximumDurationRatio, nameof(ShortVersionMaximumDurationRatio));
        ValidateUnitInterval(AlternateVersionMinimumSimilarity, nameof(AlternateVersionMinimumSimilarity));

        if (AlternateVersionMinimumSimilarity > ShortVersionMinimumSimilarity)
        {
            throw new ArgumentException("AlternateVersionMinimumSimilarity は ShortVersionMinimumSimilarity 以下である必要がある。");
        }

        if (ShortVersionMaximumMinimumCoverage >= ShortVersionMinimumMaximumCoverage)
        {
            throw new ArgumentException("Short Version判定では、小さい側Coverage上限を大きい側Coverage下限より小さくする必要がある。");
        }
    }

    private static void ValidateUnitInterval(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0d || value > 1d)
        {
            throw new ArgumentOutOfRangeException(name, value, "しきい値は0以上1以下である必要がある。");
        }
    }
}
