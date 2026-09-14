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

        var fullRootPath = Path.GetFullPath(rootPath);
        var totalFiles = scanner.GetSupportedFileCount(fullRootPath, cancellationToken);
        progress?.Report(new IncrementalScanProgress(0, totalFiles, null));

        var sessionId = await scanSessionRepository.StartAsync(fullRootPath, DateTime.UtcNow, cancellationToken);
        var total = 0;
        var processed = 0;
        var added = 0;
        var updated = 0;
        var removed = 0;
        var errors = new List<IncrementalScanError>();

        try
        {
            var storedEntries = await trackRepository.GetByRootAsync(libraryId, rootId, cancellationToken);
            var missingFingerprintIds = await trackRepository.GetTrackIdsWithoutFingerprintByRootAsync(libraryId, rootId, cancellationToken);
            var storedByPath = storedEntries.ToDictionary(
                entry => Path.GetFullPath(entry.Track.Metadata.Path),
                StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var result in scanner.Scan(fullRootPath, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;

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

                var needsFingerprint = contentChanged || missingFingerprintIds.Contains(trackId);
                if (!hasMembership && !needsFingerprint)
                {
                    // 新Membershipが既知Global Trackを再利用した場合は既存Fingerprintも共有する。
                    needsFingerprint = await trackRepository.GetFingerprintAsync(trackId, cancellationToken) is null;
                }

                if (needsFingerprint)
                {
                    try
                    {
                        var fingerprint = await fingerprintExtractor.ExtractAsync(fullPath, cancellationToken);
                        await trackRepository.SaveFingerprintAsync(trackId, fingerprint, fingerprintAlgorithm, cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                    {
                        errors.Add(new IncrementalScanError(fullPath, "Fingerprint", exception.Message));
                    }
                }

                // Fingerprint生成まで含めて1ファイル分の処理が完了した時点で進捗を進める。
                progress?.Report(new IncrementalScanProgress(total, totalFiles, fullPath));
            }

            // Membership上このRootに属していて今回の列挙で見えなかった場合でも、Global Trackの物理Pathが存在するなら
            // 他Library/Rootから同じ物理ファイルを共有している可能性がある。Root差分だけを根拠にGlobal Missingへ遷移させない。
            foreach (var storedEntry in storedEntries)
            {
                var storedPath = Path.GetFullPath(storedEntry.Track.Metadata.Path);
                if (storedEntry.Track.IsMissing
                    || seenPaths.Contains(storedPath)
                    || File.Exists(storedPath))
                {
                    continue;
                }

                await trackRepository.MarkMissingAsync(storedEntry.Track.Id, cancellationToken);
                removed++;
            }

            var summary = new ScanSessionSummary(total, processed, added, updated, removed, errors.Count);
            await scanSessionRepository.CompleteAsync(sessionId, DateTime.UtcNow, summary, cancellationToken);
            return new IncrementalScanResult(sessionId, summary, errors);
        }
        catch
        {
            var summary = new ScanSessionSummary(total, processed, added, updated, removed, errors.Count);
            await scanSessionRepository.FailAsync(sessionId, DateTime.UtcNow, summary, CancellationToken.None);
            throw;
        }
    }

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
