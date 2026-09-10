using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Scanning;

/// <summary>
/// ファイルシステムと保存済みTrackを照合し、変更されたFLACだけをDBへ反映する。
/// </summary>
public sealed class IncrementalLibraryScanService(
    ILibraryScanner scanner,
    ITrackRepository trackRepository,
    IScanSessionRepository scanSessionRepository)
{
    public async Task<IncrementalScanResult> ScanAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var fullRootPath = Path.GetFullPath(rootPath);
        var sessionId = await scanSessionRepository.StartAsync(fullRootPath, DateTime.UtcNow, cancellationToken);
        var total = 0;
        var processed = 0;
        var added = 0;
        var updated = 0;
        var removed = 0;
        var errors = 0;

        try
        {
            var storedTracks = await trackRepository.GetByRootPathAsync(fullRootPath, cancellationToken);
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
                    errors++;
                    continue;
                }

                processed++;
                var metadata = result.Metadata!;
                if (!storedByPath.TryGetValue(fullPath, out var stored))
                {
                    await trackRepository.UpsertMetadataAsync(metadata, cancellationToken);
                    added++;
                    continue;
                }

                if (stored.IsMissing || HasChanged(stored, metadata))
                {
                    await trackRepository.UpsertMetadataAsync(metadata, cancellationToken);
                    updated++;
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

            var summary = new ScanSessionSummary(total, processed, added, updated, removed, errors);
            await scanSessionRepository.CompleteAsync(sessionId, DateTime.UtcNow, summary, cancellationToken);
            return new IncrementalScanResult(sessionId, summary);
        }
        catch
        {
            var summary = new ScanSessionSummary(total, processed, added, updated, removed, errors);
            await scanSessionRepository.FailAsync(sessionId, DateTime.UtcNow, summary, CancellationToken.None);
            throw;
        }
    }

    private static bool HasChanged(StoredTrack stored, Models.AudioTrackMetadata current)
        => stored.Metadata.FileSize != current.FileSize
            || stored.Metadata.LastWriteTimeUtc != current.LastWriteTimeUtc;
}
