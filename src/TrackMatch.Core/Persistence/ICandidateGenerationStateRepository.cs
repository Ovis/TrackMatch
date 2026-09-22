using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// Library単位のCandidate Generation完了状態を永続化する境界を定義する。
/// </summary>
public interface ICandidateGenerationStateRepository
{
    /// <summary>最後に正常完了した構成を取得する。</summary>
    Task<CandidateGenerationState?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>正常完了した構成を保存する。</summary>
    Task SaveAsync(CandidateGenerationState state, CancellationToken cancellationToken = default);
}
