using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 人手で確定した候補レビューを永続化する境界を定義する。
/// </summary>
public interface ICandidateReviewRepository
{
    Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CandidateReview>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(
        CancellationToken cancellationToken = default);
}
