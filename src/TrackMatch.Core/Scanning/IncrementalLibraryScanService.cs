using System.Diagnostics;
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
    int fingerprintAlgorithm)
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
        IProgress<IncrementalScanProgress>? progress = null,
        Action<IncrementalScanDiagnostic>? diagnostic = null)
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

        var fullRootPath = Path.GetFullPath(rootPath);
        var phaseStopwatch = Stopwatch.StartNew();
        diagnostic?.Invoke(new IncrementalScanDiagnostic(IncrementalScanDiagnosticStage.CountingFilesStarted, 0, null, 0));
        var totalFiles = scanner.GetSupportedFileCount(fullRootPath, cancellationToken);
        diagnostic?.Invoke(new IncrementalScanDiagnostic(IncrementalScanDiagnosticStage.CountingFilesCompleted, 0, totalFiles, phaseStopwatch.ElapsedMilliseconds));
        progress?.Report(new IncrementalScanProgress(0, totalFiles, null));

        var sessionId = await scanSessionRepository.StartAsync(fullRootPath, DateTime.UtcNow, cancellationToken);
        var total = 0;
        var processed = 0;
        var added = 0;
        var updated = 0;
        var removed = 0;
        var errors = new List<IncrementalScanError>();
        var contentChanges = new List<ContentChangeNotice>();

        try
        {
            phaseStopwatch.Restart();
            diagnostic?.Invoke(new IncrementalScanDiagnostic(IncrementalScanDiagnosticStage.LoadingStoredTracksStarted, 0, null, 0));
            var storedEntries = await trackRepository.GetByRootAsync(libraryId, rootId, cancellationToken);
            var missingFingerprintIds = await trackRepository.GetTrackIdsWithoutFingerprintByRootAsync(libraryId, rootId, cancellationToken);
            diagnostic?.Invoke(new IncrementalScanDiagnostic(IncrementalScanDiagnosticStage.LoadingStoredTracksCompleted, 0, storedEntries.Count, phaseStopwatch.ElapsedMilliseconds));
            var storedByPath = storedEntries.ToDictionary(
                entry => Path.GetFullPath(entry.Track.Metadata.Path),
                StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            phaseStopwatch.Restart();
            diagnostic?.Invoke(new IncrementalScanDiagnostic(IncrementalScanDiagnosticStage.EnumeratingFilesStarted, 0, totalFiles, 0));
            foreach (var result in scanner.Scan(fullRootPath, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;

                // UI進捗はWPF DispatcherへPostされるため、UI Threadが占有された場合でも調査できるよう
                // 診断通知はSynchronizationContextを介さず、この実行Thread上で直接呼び出す。
                if (total % 100 == 0)
                {
                    diagnostic?.Invoke(new IncrementalScanDiagnostic(
                        IncrementalScanDiagnosticStage.EnumeratingFilesProgress,
                        total,
                        totalFiles,
                        phaseStopwatch.ElapsedMilliseconds));
                }

                var fullPath = Path.GetFullPath(result.Path);
                seenPaths.Add(fullPath);

                if (!result.IsSuccess)
                {
                    errors.Add(new IncrementalScanError(fullPath, "Metadata", result.ErrorMessage ?? "メタデータ読み取りに失敗しました。"));
                    progress?.Report(new IncrementalScanProgress(total, totalFiles, fullPath));
                    continue;
                }

                processed++;
                var metadata = result.Metadata!;
                var hasMembership = storedByPath.TryGetValue(fullPath, out var storedEntry);
                var wasMissing = hasMembership && storedEntry.Track.IsMissing;
                var contentChanged = hasMembership && HasChanged(storedEntry.Track, metadata);

                // UpsertはPath Identityを共有するため、別Libraryで既知のTrackなら同じTrackIdを再利用する。
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
                    // 新Membershipが既知Global Trackを再利用した場合は既存Fingerprintも共有する。
                    needsFingerprint = await trackRepository.GetFingerprintAsync(trackId, cancellationToken) is null;
                }

                if (needsFingerprint)
                {
                    var previousFingerprint = contentChanged || verificationPending
                        ? await trackRepository.GetFingerprintAsync(trackId, cancellationToken)
                        : null;
                    if (contentChanged && !verificationPending)
                    {
                        // Metadata更新後に解析がキャンセルされても次回Scanで判定を再開できるよう、
                        // 長時間Fingerprint解析へ入る前に利用停止と再試行必要状態だけを短いTransactionで確定する。
                        await trackRepository.MarkContentVerificationPendingAsync(trackId, cancellationToken);
                    }

                    try
                    {
                        // FileSize/mtimeだけではタグ変更と音声変更を区別できないため、Fingerprintを追加解析してから
                        // Human Verdictを維持するか無効化するかを確定する。解析中はSQLite Transactionを保持しない。
                        var fingerprint = await fingerprintExtractor.ExtractAsync(fullPath, cancellationToken);
                        if (contentChanged || verificationPending)
                        {
                            if (previousFingerprint is not null && HasSameAudioContent(previousFingerprint, fingerprint))
                            {
                                await trackRepository.MarkContentVerifiedAsync(trackId, cancellationToken);
                            }
                            else
                            {
                                // 旧Fingerprintが無い場合も内容同一を証明できないため、安全側でContent Changedとする。
                                var invalidatedReviewCount = await trackRepository
                                    .ConfirmContentChangedAndGetInvalidatedReviewCountAsync(trackId, cancellationToken);
                                contentChanges.Add(new ContentChangeNotice(fullPath, invalidatedReviewCount));
                            }
                        }

                        await trackRepository.SaveFingerprintAsync(trackId, fingerprint, fingerprintAlgorithm, cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                    {
                        if (contentChanged || verificationPending)
                        {
                            // Decoder/I/O失敗はContent Changedと断定せず、Human Verdictを保持したまま派生計算だけ停止する。
                            await trackRepository.MarkContentVerificationFailedAsync(trackId, CancellationToken.None, exception.Message);
                        }

                        errors.Add(new IncrementalScanError(fullPath, "Fingerprint", exception.Message));
                    }
                }

                // Fingerprint生成まで含めて1ファイル分の処理が完了した時点で進捗を進める。
                progress?.Report(new IncrementalScanProgress(total, totalFiles, fullPath));
            }

            diagnostic?.Invoke(new IncrementalScanDiagnostic(
                IncrementalScanDiagnosticStage.EnumeratingFilesCompleted,
                total,
                totalFiles,
                phaseStopwatch.ElapsedMilliseconds));

            // Missing確定へ入る直前をCancellationの最終受付点とする。
            // ここを通過した後に部分的なMissingだけを残すと同じRoot内で観測時点が分裂するため、
            // 対象集合はRepositoryの1 Transactionでキャンセル不可として確定する。
            cancellationToken.ThrowIfCancellationRequested();
            phaseStopwatch.Restart();
            diagnostic?.Invoke(new IncrementalScanDiagnostic(IncrementalScanDiagnosticStage.MarkingMissingStarted, total, null, 0));
            var missingTrackIds = storedEntries
                .Where(entry => !entry.Track.IsMissing
                    && !seenPaths.Contains(Path.GetFullPath(entry.Track.Metadata.Path)))
                .Select(entry => entry.Track.Id)
                .Distinct()
                .ToArray();
            await trackRepository.MarkMissingBatchAsync(missingTrackIds, CancellationToken.None);
            removed = missingTrackIds.Length;
            diagnostic?.Invoke(new IncrementalScanDiagnostic(
                IncrementalScanDiagnosticStage.MarkingMissingCompleted,
                total,
                removed,
                phaseStopwatch.ElapsedMilliseconds));

            var summary = new ScanSessionSummary(total, processed, added, updated, removed, errors.Count);
            await scanSessionRepository.CompleteAsync(sessionId, DateTime.UtcNow, summary, CancellationToken.None);
            return new IncrementalScanResult(sessionId, summary, errors, contentChanges);
        }
        catch
        {
            var summary = new ScanSessionSummary(total, processed, added, updated, removed, errors.Count);
            await scanSessionRepository.FailAsync(sessionId, DateTime.UtcNow, summary, CancellationToken.None);
            throw;
        }
    }

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

/// <summary>
/// 増分Scan内部で診断対象とする処理段階を表す。
/// </summary>
public enum IncrementalScanDiagnosticStage
{
    CountingFilesStarted,
    CountingFilesCompleted,
    LoadingStoredTracksStarted,
    LoadingStoredTracksCompleted,
    EnumeratingFilesStarted,
    EnumeratingFilesProgress,
    EnumeratingFilesCompleted,
    MarkingMissingStarted,
    MarkingMissingCompleted,
}

/// <summary>
/// UI Dispatcherの状態に依存せず、増分Scan内部の処理位置と経過時間を観測するための診断情報。
/// </summary>
/// <param name="Stage">通知対象の処理段階</param>
/// <param name="CompletedFiles">実ファイル列挙で処理を開始した件数。該当しない段階では0</param>
/// <param name="Count">段階に関連する総数または対象件数。該当しない場合はnull</param>
/// <param name="ElapsedMilliseconds">対応する処理段階の開始からの経過時間</param>
public sealed record IncrementalScanDiagnostic(
    IncrementalScanDiagnosticStage Stage,
    int CompletedFiles,
    int? Count,
    long ElapsedMilliseconds);
