using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// CandidatePairs、Pending、Generation完了状態を1つの永続化単位として確定する境界を定義する。
/// </summary>
public interface ICandidateGenerationCommitRepository
{
    /// <summary>
    /// 探索済みCandidate集合と処理状態を原子的に確定する。
    /// </summary>
    Task CommitAsync(
        bool fullRebuild,
        IReadOnlyCollection<long> affectedTrackIds,
        IReadOnlyCollection<CandidatePair> pairs,
        IReadOnlyCollection<long> completedPendingTrackIds,
        CandidateGenerationState state,
        CancellationToken cancellationToken = default);
}
