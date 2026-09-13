using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Scanning;

/// <summary>
/// ファイルシステムと保存済みTrackを照合し、変更された対応Audio FileだけをDBへ反映する。
/// </summary>
public sealed class IncrementalLibraryScanService(
    ILibraryScanner scanner,
    ITrackRepository trackRepository,
    IScanSessionRepository scanSessionRepository,
    IFingerprintExtractor fingerprintExtractor,
    int fingerprintAlgorithm)
{
    public async Task<IncrementalScanResult> ScanAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (fingerprintAlgorithm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fingerprintAlgorithm));
        }

        var fullRootPath = Path.GetFullPath(rootPath);
        var sessionId = await scanSessionRepository.StartAsync(fullRootPath, DateTime.UtcNow, cancellationToken);
        var total = 0;
        var processed = 0;
        var added = 0;
        var updated = 0;
        var removed = 0;
        var errors = new List<IncrementalScanError>();

        try
        {
            var storedTracks = await trackRepository.GetByRootPathAsync(fullRootPath, cancellationToken);
            var missingFingerprintIds = await trackRepository.GetTrackIdsWithoutFingerprintByRootPathAsync(fullRootPath, cancellationToken);
            var storedByPath = storedTracks.ToDictionary(
                track => Path.GetFullPath(track.Metadata.Path),
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
                    errors.Add(new IncrementalScanError(fullPath, "Metadata", result.ErrorMessage ?? "メタデータ読み取りに失敗した。"));
                    continue;
                }

                processed++;
                var metadata = result.Metadata!;
                var isNew = !storedByPath.TryGetValue(fullPath, out var stored);
                var fileChanged = !isNew && HasFileChanged(stored!, metadata);
                var metadataChanged = !isNew && stored!.Metadata.Year != metadata.Year;
                var needsMetadataUpdate = isNew || stored!.IsMissing || fileChanged || metadataChanged;
                var needsFingerprintRefresh = isNew || stored.IsMissing || fileChanged;
                long trackId;

                if (isNew)
                {
                    trackId = await trackRepository.UpsertMetadataAsync(metadata, cancellationToken);
                    added++;
                }
                else if (needsMetadataUpdate)
                {
                    trackId = stored!.Id;
                    await trackRepository.UpsertMetadataAsync(metadata, cancellationToken);

                    if (needsFingerprintRefresh)
                    {
                        // Missingからの復活やファイル実体の変更時だけ旧Fingerprintを破棄する。
                        // Year追加などタグ情報だけの補完では音声データは変わらないため、重いFingerprint再生成を避ける。
                        await trackRepository.DeleteFingerprintAsync(trackId, cancellationToken);
                    }

                    updated++;
                }
                else
                {
                    trackId = stored!.Id;
                }

                var needsFingerprint = needsFingerprintRefresh || missingFingerprintIds.Contains(trackId);
                if (!needsFingerprint)
                {
                    continue;
                }

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

            foreach (var stored in storedTracks)
            {
                if (stored.IsMissing || seenPaths.Contains(Path.GetFullPath(stored.Metadata.Path)))
                {
                    continue;
                }

                await trackRepository.MarkMissingAsync(stored.Id, cancellationToken);
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

    private static bool HasFileChanged(StoredTrack stored, Models.AudioTrackMetadata current)
        => stored.Metadata.FileSize != current.FileSize
            || stored.Metadata.LastWriteTimeUtc != current.LastWriteTimeUtc;
}
