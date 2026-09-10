using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Trash;

/// <summary>
/// ConfirmedDuplicateレビューからReject側を求め、安全条件を確認してTrashへ移動する。
/// </summary>
public sealed class RejectedTrackTrashService(
    ICandidateReviewRepository reviewRepository,
    ITrackLookupRepository trackLookupRepository,
    ITrackRepository trackRepository,
    ITrackFileOperations fileOperations)
{
    public async Task<RejectedTrackTrashResult> ProcessAsync(
        string libraryRoot,
        string trashRoot,
        bool execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(trashRoot);

        var fullLibraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        var fullTrashRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trashRoot));
        if (IsUnderRoot(fullTrashRoot, fullLibraryRoot))
        {
            throw new ArgumentException("Trashルートはライブラリルートの外側に指定する必要がある。", nameof(trashRoot));
        }

        var reviews = await reviewRepository.GetAllAsync(cancellationToken);
        var confirmed = reviews
            .Where(review => review.Decision == CandidateReviewDecision.ConfirmedDuplicate)
            .ToArray();
        var keepTrackIds = confirmed
            .Select(review => review.KeepTrackId!.Value)
            .ToHashSet();
        var rejectTrackIds = confirmed
            .Select(GetRejectTrackId)
            .Distinct()
            .Order()
            .ToArray();

        var items = new List<RejectedTrackMoveItem>(rejectTrackIds.Length);
        foreach (var trackId in rejectTrackIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (keepTrackIds.Contains(trackId))
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    null,
                    null,
                    RejectedTrackMoveStatus.ReviewConflict,
                    "同じTrackが別のConfirmedDuplicateレビューでKeepにも指定されている。"));
                continue;
            }

            var track = await trackLookupRepository.GetByIdAsync(trackId, cancellationToken);
            if (track is null)
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    null,
                    null,
                    RejectedTrackMoveStatus.TrackNotFound,
                    "DBにTrackが存在しない。"));
                continue;
            }

            var sourcePath = Path.GetFullPath(track.Metadata.Path);
            if (!IsUnderRoot(sourcePath, fullLibraryRoot))
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    sourcePath,
                    null,
                    RejectedTrackMoveStatus.OutsideLibraryRoot,
                    "Trackが指定ライブラリルート配下に存在しない。"));
                continue;
            }

            var relativePath = Path.GetRelativePath(fullLibraryRoot, sourcePath);
            var destinationPath = Path.GetFullPath(Path.Combine(fullTrashRoot, relativePath));
            if (track.IsMissing)
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    sourcePath,
                    destinationPath,
                    RejectedTrackMoveStatus.AlreadyMissing,
                    "Trackは既にMissingとして記録されている。"));
                continue;
            }

            if (!fileOperations.FileExists(sourcePath))
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    sourcePath,
                    destinationPath,
                    RejectedTrackMoveStatus.SourceMissing,
                    "移動元ファイルが存在しない。"));
                continue;
            }

            if (fileOperations.FileExists(destinationPath))
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    sourcePath,
                    destinationPath,
                    RejectedTrackMoveStatus.DestinationExists,
                    "Trash側に同じ相対パスのファイルが既に存在する。"));
                continue;
            }

            if (!execute)
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    sourcePath,
                    destinationPath,
                    RejectedTrackMoveStatus.Ready));
                continue;
            }

            try
            {
                fileOperations.Move(sourcePath, destinationPath);
                await trackRepository.DeleteFingerprintAsync(trackId, cancellationToken);
                await trackRepository.MarkMissingAsync(trackId, cancellationToken);
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    sourcePath,
                    destinationPath,
                    RejectedTrackMoveStatus.Moved));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    sourcePath,
                    destinationPath,
                    RejectedTrackMoveStatus.Failed,
                    exception.Message));
            }
        }

        return new RejectedTrackTrashResult(items, execute);
    }

    private static long GetRejectTrackId(CandidateReview review)
        => review.KeepTrackId == review.Pair.TrackIdA
            ? review.Pair.TrackIdB
            : review.Pair.TrackIdA;

    private static bool IsUnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
