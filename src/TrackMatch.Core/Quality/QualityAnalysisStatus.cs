namespace TrackMatch.Core.Quality;

/// <summary>
/// 音質解析結果のライフサイクル状態を表す。
/// </summary>
public enum QualityAnalysisStatus
{
    NotAnalyzed = 0,
    Analyzing = 1,
    Analyzed = 2,
    Failed = 3,
    Unsupported = 4,
}
