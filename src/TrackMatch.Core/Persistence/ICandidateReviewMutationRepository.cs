using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 候補レビューを未確定へ戻す操作を含む更新用Repository境界を定義する。
/// </summary>
public interface ICandidateReviewMutationRepository : ICandidateReviewRepository
{
    /// <summary>指定候補のレビューを削除し、未レビュー状態へ戻す。</summary>
    Task DeleteAsync(CandidatePairKey pair, CancellationToken cancellationToken = default);
}
