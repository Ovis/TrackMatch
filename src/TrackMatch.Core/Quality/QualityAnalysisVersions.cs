namespace TrackMatch.Core.Quality;

/// <summary>
/// 保存済み品質解析キャッシュの互換性判定に使用するアルゴリズムバージョンを定義する。
/// </summary>
public static class QualityAnalysisVersions
{
    /// <summary>
    /// Track単体解析アルゴリズムの現在バージョン。
    /// </summary>
    public const int TrackQualityAnalysis = 1;

    /// <summary>
    /// Candidate固有比較アルゴリズムの現在バージョン。
    /// </summary>
    public const int CandidateQualityComparison = 1;
}
