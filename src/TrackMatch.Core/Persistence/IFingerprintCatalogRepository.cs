namespace TrackMatch.Core.Persistence;

/// <summary>
/// 候補生成で利用する保存済みFingerprintの読み取り境界を定義する。
/// </summary>
public interface IFingerprintCatalogRepository
{
    Task<IReadOnlyList<StoredFingerprint>> GetActiveAsync(
        int algorithm,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredFingerprintState>> GetActiveStatesAsync(
        int algorithm,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredFingerprint>> GetActiveByTrackIdsAsync(
        int algorithm,
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default);
}
