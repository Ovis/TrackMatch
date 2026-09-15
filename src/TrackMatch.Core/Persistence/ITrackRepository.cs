using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// Global Track、Library Membership、Fingerprintの永続化境界を定義する。
/// </summary>
public interface ITrackRepository
{
    /// <summary>
    /// 正規化物理PathをIdentityとしてTrackメタデータを追加または更新する。
    /// </summary>
    Task<long> UpsertMetadataAsync(AudioTrackMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定PathのGlobal Trackを取得する。
    /// </summary>
    Task<StoredTrack?> GetByPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定Library Root由来のMembershipとGlobal Trackを取得する。
    /// </summary>
    Task<IReadOnlyList<(StoredLibraryTrack Membership, StoredTrack Track)>> GetByRootAsync(
        long libraryId,
        long rootId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定Library Root由来のMembershipに属し、Fingerprintが未保存のTrack IDを取得する。
    /// </summary>
    Task<IReadOnlySet<long>> GetTrackIdsWithoutFingerprintByRootAsync(
        long libraryId,
        long rootId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 正常なRoot Scanで確認したTrackのMembershipを作成または更新する。
    /// </summary>
    Task EnsureMembershipAsync(
        long libraryId,
        long rootId,
        long trackId,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// TrackをGlobalに欠落状態として記録する。
    /// </summary>
    Task MarkMissingAsync(long trackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 正常完了したRoot ScanのMissing確定対象を一括でGlobal Missingへ遷移させる。
    /// 永続化実装では、この集合を部分適用しないTransaction境界として扱う。
    /// </summary>
    Task MarkMissingBatchAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default);

    Task SaveFingerprintAsync(
        long trackId,
        AudioFingerprint fingerprint,
        int algorithm,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存済みFingerprintを無効化する。
    /// </summary>
    Task DeleteFingerprintAsync(long trackId, CancellationToken cancellationToken = default);

    Task<AudioFingerprint?> GetFingerprintAsync(long trackId, CancellationToken cancellationToken = default);
}
