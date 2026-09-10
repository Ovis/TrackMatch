using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 詳細比較へ渡す候補Trackペアの永続化境界を定義する。
/// </summary>
public interface ICandidatePairRepository
{
    Task ReplaceAllAsync(
        IReadOnlyCollection<CandidatePair> pairs,
        CancellationToken cancellationToken = default);
}
