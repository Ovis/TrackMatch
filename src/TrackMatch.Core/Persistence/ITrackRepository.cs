using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 音源メタデータとFingerprintの永続化境界を定義する。
/// </summary>
public interface ITrackRepository
{
    Task<long> UpsertMetadataAsync(AudioTrackMetadata metadata, CancellationToken cancellationToken = default);

    Task<StoredTrack?> GetByPathAsync(string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredTrack>> GetByRootPathAsync(
        string rootPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<long>> GetTrackIdsWithoutFingerprintByRootPathAsync(
        string rootPath,
        CancellationToken cancellationToken = default);

    Task MarkMissingAsync(long trackId, CancellationToken cancellationToken = default);

    Task SaveFingerprintAsync(
        long trackId,
        AudioFingerprint fingerprint,
        int algorithm,
        CancellationToken cancellationToken = default);

    Task DeleteFingerprintAsync(long trackId, CancellationToken cancellationToken = default);

    Task<AudioFingerprint?> GetFingerprintAsync(long trackId, CancellationToken cancellationToken = default);
}
