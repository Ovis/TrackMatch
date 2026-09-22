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
    /// Metadata更新とLibrary Membership確立を1つのScan操作として実行し、後続判定に必要な状態を返す。
    /// </summary>
    async Task<PreparedTrackScanState> PrepareTrackForScanAsync(
        AudioTrackMetadata metadata,
        long libraryId,
        long rootId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var trackId = await UpsertMetadataAsync(metadata, cancellationToken);
        await EnsureMembershipAsync(libraryId, rootId, trackId, relativePath, cancellationToken);
        return new PreparedTrackScanState(
            trackId,
            await GetFingerprintAsync(trackId, cancellationToken) is not null,
            await IsContentVerificationPendingAsync(trackId, cancellationToken));
    }

    /// <summary>
    /// 指定PathのGlobal Trackを取得する。
    /// </summary>
    Task<StoredTrack?> GetByPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 増分Scan開始時に必要なRoot内Track状態を一括取得する。
    /// </summary>
    /// <remarks>
    /// 大規模Libraryで同じRootをFingerprint有無・Verification状態ごとに再照会しないためのScan専用read model。
    /// </remarks>
    async Task<IReadOnlyList<RootScanTrackState>> GetRootScanStateAsync(
        long libraryId,
        long rootId,
        CancellationToken cancellationToken = default)
    {
        var entries = await GetByRootAsync(libraryId, rootId, cancellationToken);
        var missingFingerprintIds = await GetTrackIdsWithoutFingerprintByRootAsync(libraryId, rootId, cancellationToken);
        var verificationPendingIds = await GetContentVerificationPendingTrackIdsByRootAsync(libraryId, rootId, cancellationToken);
        return entries.Select(entry => new RootScanTrackState(
            entry.Membership,
            entry.Track,
            !missingFingerprintIds.Contains(entry.Track.Id),
            verificationPendingIds.Contains(entry.Track.Id))).ToArray();
    }

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
    /// 指定RootでContent Verificationの再試行が必要なTrack IDを一括取得する。
    /// </summary>
    Task<IReadOnlySet<long>> GetContentVerificationPendingTrackIdsByRootAsync(
        long libraryId,
        long rootId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());

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

    /// <summary>Content Verificationを後続Scanで再試行する必要があるか確認する。</summary>
    Task<bool> IsContentVerificationPendingAsync(long trackId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>追加解析開始前に対象TrackのHuman Verdictを一時利用停止し、キャンセル後も再試行対象として残す。</summary>
    Task MarkContentVerificationPendingAsync(long trackId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>追加解析失敗により対象TrackのHuman Verdictを一時利用停止する。</summary>
    Task MarkContentVerificationFailedAsync(
        long trackId,
        CancellationToken cancellationToken = default,
        string? error = null)
        => Task.CompletedTask;

    /// <summary>音声内容変更が確定したTrackのContent依存状態を無効化する。</summary>
    Task ConfirmContentChangedAsync(long trackId, CancellationToken cancellationToken = default)
        => DeleteFingerprintAsync(trackId, cancellationToken);

    /// <summary>音声内容変更を確定し、自動解除したHuman Verdict件数を返す。</summary>
    async Task<int> ConfirmContentChangedAndGetInvalidatedReviewCountAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        await ConfirmContentChangedAsync(trackId, cancellationToken);
        return 0;
    }

    /// <summary>音声内容同一を確認し、一時利用停止中のHuman Verdictを再有効化する。</summary>
    Task MarkContentVerifiedAsync(long trackId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// Root Scan開始時に一括取得するTrack状態を表す。
/// </summary>
public sealed record RootScanTrackState(
    StoredLibraryTrack Membership,
    StoredTrack Track,
    bool HasFingerprint,
    bool VerificationPending);

/// <summary>
/// Scan用のMetadata/Membership更新後に必要なTrack状態を表す。
/// </summary>
public sealed record PreparedTrackScanState(long TrackId, bool HasFingerprint, bool VerificationPending);
