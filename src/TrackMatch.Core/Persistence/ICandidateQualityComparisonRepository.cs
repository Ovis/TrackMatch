using TrackMatch.Core.Quality;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// Candidate固有の音質比較キャッシュを永続化する。
/// </summary>
public interface ICandidateQualityComparisonRepository
{
    /// <summary>
    /// 指定Candidateの比較結果を取得する。
    /// </summary>
    Task<CandidateQualityComparison?> GetAsync(
        long trackIdA,
        long trackIdB,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Candidate固有の比較状態または比較結果を保存する。
    /// </summary>
    Task UpsertAsync(
        CandidateQualityComparison comparison,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 比較条件変更や手動再解析に備えて指定Candidateのキャッシュを削除する。
    /// </summary>
    Task DeleteAsync(long trackIdA, long trackIdB, CancellationToken cancellationToken = default);
}
