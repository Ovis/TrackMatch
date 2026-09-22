using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Scanning;

/// <summary>
/// Library Rootのファイルシステム状態をGlobal TrackとLibrary Membershipへ増分反映する。
/// </summary>
public sealed class IncrementalLibraryScanService(
    ILibraryScanner scanner,
    ITrackRepository trackRepository,
    IScanSessionRepository scanSessionRepository,
    IFingerprintExtractor fingerprintExtractor,
    int fingerprintAlgorithm,
    int maxConcurrentFingerprintExtractions = 4)
{
    /// <summary>
    /// 指定Library Rootを増分走査し、Global Track・Membership・Fingerprintを更新する。
    /// </summary>
    /// <remarks>
    /// Root列挙が最後まで正常完了した場合だけMissingを確定する。アクセス失敗やキャンセルで処理が中断した場合、
    /// 見えなかったTrackをMissingにすることはない。一方、完了済みTrack単位の更新はRollbackせず再利用する。
    /// </remarks>
    public async Task<IncrementalScanResult> ScanAsync(
        long libraryId,
        long rootId,
        string rootPath,
        CancellationToken cancellationToken = default,
        IProgress<IncrementalScanProgress>? progress = null)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        if (rootId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rootId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (fingerprintAlgorithm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fingerprintAlgorithm));
        }

        if (maxConcurrentFingerprintExtractions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentFingerprintExtractions));
        }

        var fullRootPath = Path.GetFullPath(rootPath);
        // 件数表示のためだけにNAS全体を事前走査すると大規模Libraryでは列挙コストが二重になる。
        // 進捗総数は未確定(null)として開始し、実処理と同じ1回の列挙だけを行う。
        int? totalFiles = null;
        progress?.Report(new IncrementalScanProgress(0, totalFiles, null));

        var sessionId = await scanSessionRepository.StartAsync(fullRootPath, DateTime.UtcNow, cancellationToken);
        var total = 0;
        var completedFiles = 0;
        var processed = 0;
        var added = 0;
        var updated = 0;
        var removed = 0;
        var errors = new List<IncrementalScanError>();
        var contentChanges = new List<ContentChangeNotice>();
        var pendingFingerprints = new List<PendingFingerprintExtraction>(maxConcurrentFingerprintExtractions);

        try
        {
            // Root内Track・Fingerprint有無・Verification状態を1回のRepository readで取得し、
            // 大規模LibraryのScan開始時に同じ集合を複数回SQLiteへ問い合わせない。
            var rootScanStates = await trackRepository.GetRootScanStateAsync(libraryId, rootId, cancellationToken);
            var storedEntries = rootScanStates
                .Select(state => (state.Membership, state.Track))
                .ToArray();
            var missingFingerprintIds = rootScanStates
                .Where(state => !state.HasFingerprint)
                .Select(state => state.Track.Id)
                .ToHashSet();
            var verificationPendingTrackIds = rootScanStates
                .Where(state => state.VerificationPending)
                .Select(state => state.Track.Id)
                .ToHashSet();
            var storedByPath = rootScanStates.ToDictionary(
                state => Path.GetFullPath(state.Track.Metadata.Path),
                state => (state.Membership, state.Track),
                StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 未変更・正常状態の既知FileはMetadata Decodeと毎FileのSQLite更新を省略する。
            // Missing復帰、Fingerprint欠落、Verification再試行は状態更新が必要なので必ず通常経路へ戻す。
            bool CanSkipMetadata(LibraryFileSnapshot snapshot)
            {
                var path = Path.GetFullPath(snapshot.Path);
                return storedByPath.TryGetValue(path, out var entry)
                    && !entry.Track.IsMissing
                    && !missingFingerprintIds.Contains(entry.Track.Id)
                    && !verificationPendingTrackIds.Contains(entry.Track.Id)
                    && entry.Track.Metadata.FileSize == snapshot.FileSize
                    && entry.Track.Metadata.LastWriteTimeUtc == snapshot.LastWriteTimeUtc;
            }

            foreach (var result in scanner.Scan(fullRootPath, CanSkipMetadata, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;

                var fullPath = Path.GetFullPath(result.Path);
                seenPaths.Add(fullPath);

                if (result.MetadataSkipped)
                {
                    processed++;
                    progress?.Report(new IncrementalScanProgress(++completedFiles, totalFiles, fullPath));
                    continue;
                }

                if (!result.IsSuccess)
                {
                    errors.Add(new IncrementalScanError(fullPath, "Metadata", result.ErrorMessage ?? "メタデータ読み取りに失敗しました。"));
                    progress?.Report(new IncrementalScanProgress(++completedFiles, totalFiles, fullPath));
                    continue;
                }

                processed++;
                var metadata = result.Metadata!;
                var hasMembership = storedByPath.TryGetValue(fullPath, out var storedEntry);
                var wasMissing = hasMembership && storedEntry.Track.IsMissing;
                var contentChanged = hasMembership && HasChanged(storedEntry.Track, metadata);

                // MetadataとSQLite更新は従来どおり直列に保ち、支配的なfpcalcだけを並列化する。
                // Path Identityを共有するため、別Libraryで既知のTrackなら同じTrackIdを再利用する。
                var trackId = await trackRepository.UpsertMetadataAsync(metadata, cancellationToken);
                var relativePath = Path.GetRelativePath(fullRootPath, fullPath);
                await trackRepository.EnsureMembershipAsync(libraryId, rootId, trackId, relativePath, cancellationToken);

                if (!hasMembership)
                {
                    added++;
                }
                else if (wasMissing || contentChanged)
                {
                    updated++;
                }

                var verificationPending = await trackRepository.IsContentVerificationPendingAsync(trackId, cancellationToken);
                var needsFingerprint = contentChanged || verificationPending || missingFingerprintIds.Contains(trackId);
                if (!hasMembership && !needsFingerprint)
                {
                    needsFingerprint = await trackRepository.GetFingerprintAsync(trackId, cancellationToken) is null;
                }

                if (!needsFingerprint)
                {
                    progress?.Report(new IncrementalScanProgress(++completedFiles, totalFiles, fullPath));
                    continue;
                }

                var previousFingerprint = contentChanged || verificationPending
                    ? await trackRepository.GetFingerprintAsync(trackId, cancellationToken)
                    : null;
                if (contentChanged && !verificationPending)
                {
                    // 長時間のFingerprint解析前に利用停止状態だけを確定し、キャンセル後も次回Scanで再試行できるようにする。
                    await trackRepository.MarkContentVerificationPendingAsync(trackId, cancellationToken);
                }

                // 全ファイル分のTaskを作らず、実行中fpcalc数だけを小さなbounded pipelineとして保持する。
                // スロットが埋まったら最初に完了した結果を直列Commitしてから次のfpcalcを開始する。
                pendingFingerprints.Add(new PendingFingerprintExtraction(
                    fullPath,
                    trackId,
                    contentChanged,
                    verificationPending,
                    previousFingerprint,
                    fingerprintExtractor.ExtractAsync(fullPath, cancellationToken)));

                if (pendingFingerprints.Count >= maxConcurrentFingerprintExtractions)
                {
                    var completedPath = await CompleteNextFingerprintAsync(
                        pendingFingerprints,
                        trackRepository,
                        fingerprintAlgorithm,
                        errors,
                        contentChanges,
                        cancellationToken);
                    progress?.Report(new IncrementalScanProgress(++completedFiles, totalFiles, completedPath));
                }
            }

            while (pendingFingerprints.Count != 0)
            {
                var completedPath = await CompleteNextFingerprintAsync(
                    pendingFingerprints,
                    trackRepository,
                    fingerprintAlgorithm,
                    errors,
                    contentChanges,
                    cancellationToken);
                progress?.Report(new IncrementalScanProgress(++completedFiles, totalFiles, completedPath));
            }

            // Missing確定へ入る直前をCancellationの最終受付点とする。
            // ここを通過した後に部分的なMissingだけを残すと同じRoot内で観測時点が分裂するため、
            // 対象集合はRepositoryの1 Transactionでキャンセル不可として確定する。
            cancellationToken.ThrowIfCancellationRequested();
            var missingTrackIds = storedEntries
                .Where(entry => !entry.Track.IsMissing
                    && !seenPaths.Contains(Path.GetFullPath(entry.Track.Metadata.Path)))
                .Select(entry => entry.Track.Id)
                .Distinct()
                .ToArray();
            await trackRepository.MarkMissingBatchAsync(missingTrackIds, CancellationToken.None);
            removed = missingTrackIds.Length;

            var summary = new ScanSessionSummary(total, processed, added, updated, removed, errors.Count);
            await scanSessionRepository.CompleteAsync(sessionId, DateTime.UtcNow, summary, CancellationToken.None);
            return new IncrementalScanResult(sessionId, summary, errors, contentChanges);
        }
        catch
        {
            // 呼び出し元へ元の失敗理由を返す前に、同じCancellationTokenで停止中のfpcalc Taskをすべて観測する。
            // 観測せずにScanを抜けると、並列Taskの例外が後から未観測例外として残る可能性がある。
            await ObservePendingFingerprintTasksAsync(pendingFingerprints);
            var summary = new ScanSessionSummary(total, processed, added, updated, removed, errors.Count);
            await scanSessionRepository.FailAsync(sessionId, DateTime.UtcNow, summary, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 中断時に残っているFingerprint生成Taskをすべて観測する。
    /// </summary>
    private static async Task ObservePendingFingerprintTasksAsync(IReadOnlyCollection<PendingFingerprintExtraction> pending)
    {
        foreach (var item in pending)
        {
            try
            {
                await item.Task;
            }
            catch
            {
                // Scanを中断させた元の例外を優先するため、並列Task側の後続例外は観測だけ行う。
            }
        }
    }

    /// <summary>
    /// 実行中のFingerprint生成から最初に完了した1件を取り出し、DB更新を直列に確定する。
    /// </summary>
    private static async Task<string> CompleteNextFingerprintAsync(
        List<PendingFingerprintExtraction> pending,
        ITrackRepository trackRepository,
        int fingerprintAlgorithm,
        List<IncrementalScanError> errors,
        List<ContentChangeNotice> contentChanges,
        CancellationToken cancellationToken)
    {
        var completedTask = await Task.WhenAny(pending.Select(item => item.Task));
        var itemIndex = pending.FindIndex(item => ReferenceEquals(item.Task, completedTask));
        var item = pending[itemIndex];
        pending.RemoveAt(itemIndex);

        try
        {
            var fingerprint = await item.Task;
            if (item.ContentChanged || item.VerificationPending)
            {
                if (item.PreviousFingerprint is not null && HasSameAudioContent(item.PreviousFingerprint, fingerprint))
                {
                    await trackRepository.MarkContentVerifiedAsync(item.TrackId, cancellationToken);
                }
                else
                {
                    var invalidatedReviewCount = await trackRepository
                        .ConfirmContentChangedAndGetInvalidatedReviewCountAsync(item.TrackId, cancellationToken);
                    contentChanges.Add(new ContentChangeNotice(item.Path, invalidatedReviewCount));
                }
            }

            await trackRepository.SaveFingerprintAsync(item.TrackId, fingerprint, fingerprintAlgorithm, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            if (item.ContentChanged || item.VerificationPending)
            {
                await trackRepository.MarkContentVerificationFailedAsync(item.TrackId, CancellationToken.None, exception.Message);
            }

            errors.Add(new IncrementalScanError(item.Path, "Fingerprint", exception.Message));
        }

        return item.Path;
    }

    private sealed record PendingFingerprintExtraction(
        string Path,
        long TrackId,
        bool ContentChanged,
        bool VerificationPending,
        AudioFingerprint? PreviousFingerprint,
        Task<AudioFingerprint> Task);

    private static bool HasSameAudioContent(AudioFingerprint previous, AudioFingerprint current)
        => previous.Duration == current.Duration
            && previous.Values.SequenceEqual(current.Values);

    private static bool HasChanged(StoredTrack stored, Models.AudioTrackMetadata current)
        => stored.Metadata.FileSize != current.FileSize
            || stored.Metadata.LastWriteTimeUtc != current.LastWriteTimeUtc;
}

/// <summary>
/// 対象フォルダ走査のファイル単位進捗を表す。
/// </summary>
/// <param name="CompletedFiles">処理を完了した対応音声ファイル数</param>
/// <param name="TotalFiles">事前取得できた対応音声ファイル総数。不明な場合はnull</param>
/// <param name="CurrentPath">直近に処理を完了したファイルのパス</param>
public sealed record IncrementalScanProgress(
    int CompletedFiles,
    int? TotalFiles,
    string? CurrentPath);
