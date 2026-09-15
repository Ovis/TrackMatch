namespace TrackMatch.Core.Candidates;

/// <summary>
/// Candidate Comparisonの互換性判定に使用するAlgorithm Versionを定義する。
/// </summary>
public static class CandidateComparisonAlgorithmVersion
{
    /// <summary>
    /// 現在のFingerprint詳細比較Algorithm Version。
    /// 比較ロジックや比較値の意味が変わった場合は必ず更新する。
    /// </summary>
    public const int Current = 1;
}
