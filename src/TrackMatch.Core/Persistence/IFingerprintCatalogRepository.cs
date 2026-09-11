namespace TrackMatch.Core.Persistence;

/// <summary>
/// 候補生成で利用する保存済みFingerprintの読み取り境界を定義する。
/// </summary>
public interface IFingerprintCatalogRepository
{
    Task<IReadOnlyList<StoredFingerprint>> GetActiveAsync(
        int algorithm,
        CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<StoredFingerprintState>> GetActiveStatesAsync(
        int algorithm,
        CancellationToken cancellationToken = default)
        => (await GetActiveAsync(algorithm, cancellationToken))
            .Select(item => new StoredFingerprintState(item.TrackId, item.Algorithm, item.ExtractedAtUtc))
            .ToArray();

    async Task<IReadOnlyList<StoredFingerprint>> GetActiveByTrackIdsAsync(
        int algorithm,
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        var targets = trackIds.ToHashSet();
        return (await GetActiveAsync(algorithm, cancellationToken))
            .Where(item => targets.Contains(item.TrackId))
            .ToArray();
    }
}
