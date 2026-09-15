namespace TrackMatch.Core.Persistence;

/// <summary>
/// LibraryTrackの永続的なCandidate Generation Pending状態をCandidate生成処理へ公開する。
/// </summary>
public interface ICandidateGenerationWorkRepository
{
    /// <summary>
    /// 現行Candidate Generation Versionで再探索が必要なTrack IDを取得する。
    /// </summary>
    Task<IReadOnlySet<long>> GetPendingTrackIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Candidate Generationが正常完了したTrackのPendingを解除する。
    /// </summary>
    Task MarkCompletedAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default);
}
