using TrackMatch.Core.Quality;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// Track単体の音質解析キャッシュを永続化する。
/// </summary>
public interface ITrackQualityAnalysisRepository
{
    /// <summary>
    /// 指定Trackの解析結果を取得する。
    /// </summary>
    Task<TrackQualityAnalysis?> GetAsync(long trackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Track単体の解析状態または解析結果を保存する。
    /// </summary>
    Task UpsertAsync(TrackQualityAnalysis analysis, CancellationToken cancellationToken = default);

    /// <summary>
    /// ファイル変更や手動再解析に備えて指定Trackのキャッシュを削除する。
    /// </summary>
    Task DeleteAsync(long trackId, CancellationToken cancellationToken = default);
}
