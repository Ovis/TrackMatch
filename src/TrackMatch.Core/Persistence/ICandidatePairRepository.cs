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

    async Task ReplaceForTracksAsync(
        IReadOnlyCollection<long> trackIds,
        IReadOnlyCollection<CandidatePair> pairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        ArgumentNullException.ThrowIfNull(pairs);
        var affected = trackIds.ToHashSet();
        if (affected.Count == 0)
        {
            return;
        }

        var preserved = (await GetAllAsync(cancellationToken))
            .Where(pair => !affected.Contains(pair.TrackIdA) && !affected.Contains(pair.TrackIdB));
        await ReplaceAllAsync(preserved.Concat(pairs).ToArray(), cancellationToken);
    }

    Task<IReadOnlyList<CandidatePair>> GetAllAsync(CancellationToken cancellationToken = default);
}
