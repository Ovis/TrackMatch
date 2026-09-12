namespace TrackMatch.Core.Quality;

/// <summary>
/// 音声ファイルを解析し、Track単体の品質測定値を生成する。
/// </summary>
public interface ITrackQualityAnalyzer
{
    /// <summary>
    /// 指定した音声ファイルを解析する。
    /// </summary>
    /// <param name="trackId">解析対象Trackの永続化ID</param>
    /// <param name="path">解析対象音声ファイルのフルパス</param>
    Task<TrackQualityAnalysis> AnalyzeAsync(
        long trackId,
        string path,
        CancellationToken cancellationToken = default);
}
