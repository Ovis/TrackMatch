using TrackMatch.Core.Classification;

namespace TrackMatch.Application;

/// <summary>
/// GUIの「スキャン・分析」で自動分類するときに使用する標準しきい値を提供する。
/// </summary>
/// <remarks>
/// Coreの分類器は用途ごとにしきい値を校正できるよう既定値を持たない。
/// 一方、GUIでは利用者が別途プロファイルを用意しなくても一連の分析を完了できる必要があるため、
/// アプリケーション層の運用値としてここに集約する。将来設定画面から調整可能にする場合も、
/// Coreへ既定値を持ち込まずこの境界で差し替える。
/// </remarks>
public static class AutomaticRelationshipClassificationProfile
{
    /// <summary>
    /// GUIの自動分類で使用する標準プロファイルを取得する。
    /// </summary>
    public static RelationshipThresholdProfile Default { get; } = new(
        DuplicateMinimumSimilarity: 0.90,
        DuplicateMinimumCoverage: 0.95,
        DuplicateMinimumDurationRatio: 0.95,
        ShortVersionMinimumSimilarity: 0.85,
        ShortVersionMinimumMaximumCoverage: 0.95,
        ShortVersionMaximumMinimumCoverage: 0.60,
        ShortVersionMaximumDurationRatio: 0.60,
        AlternateVersionMinimumSimilarity: 0.65);
}
