namespace TrackMatch.Application;

/// <summary>
/// Track単体品質解析の全体進捗を表す。
/// </summary>
public sealed record TrackQualityAnalysisProgress(
    int CompletedCount,
    int TotalCount,
    int FailedCount)
{
    /// <summary>
    /// 未完了Track数を取得する。
    /// </summary>
    public int RemainingCount => Math.Max(0, TotalCount - CompletedCount);
}
