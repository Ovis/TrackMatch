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
    /// 自分が開始したAnalyzing状態がまだCurrentの場合だけ解析結果へ置換する。
    /// </summary>
    /// <param name="analysis">保存する最終解析結果</param>
    /// <param name="analyzingStartedAtUtc">Analyzing状態へ保存した解析開始時刻</param>
    /// <returns>この解析がCurrent状態を所有しており、結果を保存できた場合true</returns>
    Task<bool> TryCompleteAnalyzingAsync(
        TrackQualityAnalysis analysis,
        DateTime analyzingStartedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 自分が開始したAnalyzing状態がまだCurrentの場合だけ削除する。
    /// </summary>
    /// <remarks>
    /// 新しい解析セッションやForce Reanalysisが既に状態を変更している場合は何も変更しない。
    /// </remarks>
    Task<bool> DeleteAnalyzingAsync(
        long trackId,
        DateTime analyzingStartedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ファイル変更や手動再解析に備えて指定Trackのキャッシュを削除する。
    /// </summary>
    Task DeleteAsync(long trackId, CancellationToken cancellationToken = default);
}
