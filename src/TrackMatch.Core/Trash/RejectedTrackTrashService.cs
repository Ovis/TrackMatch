using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Trash;

/// <summary>
/// 選択LibraryのConfirmedDuplicateレビューからReject側を求め、安全条件を確認してApp-wide Trashへ移動する。
/// </summary>
public sealed class RejectedTrackTrashService(
    ICandidateReviewRepository reviewRepository,
    ITrackLookupRepository trackLookupRepository,
    ITrackRepository trackRepository,
    ITrackFileOperations fileOperations)
{
    /// <summary>
    /// 指定Libraryに属するReject TrackをPreviewまたはTrashへ移動する。
    /// </summary>
    public async Task<RejectedTrackTrashResult> ProcessAsync(
        long libraryId,
        string trashRoot,
        bool execute,
        TrashDestinationCollisionBehavior collisionBehavior = TrashDestinationCollisionBehavior.Skip,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(trashRoot);
        var fullTrashRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trashRoot));
        var reviews = await reviewRepository.GetAllAsync(cancellationToken);
        var confirmed = reviews
            .Where(review => review.Decision == CandidateReviewDecision.ConfirmedDuplicate)
            .ToArray();
        var keepTrackIds = confirmed.Select(review => review.KeepTrackId!.Value).ToHashSet();
        var rejectTrackIds = confirmed.Select(GetRejectTrackId).Distinct().Order().ToArray();

        var items = new List<RejectedTrackMoveItem>(rejectTrackIds.Length);
        foreach (var trackId in rejectTrackIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = await trackLookupRepository.GetByIdAsync(trackId, cancellationToken);

            // Review Repositoryは歴史的に全Library共通APIなので、Track所属で選択Libraryへscopeする。
            // TrackIdはDB全体で一意なため、別LibraryのReviewをここで除外すればPair混入は起きない。
            if (track is null || track.LibraryId != libraryId)
            {
                continue;
            }

            var sourcePath = Path.GetFullPath(track.Metadata.Path);
            var destinationPath = TrashPathRules.CreateDestinationPath(fullTrashRoot, sourcePath);

            if (keepTrackIds.Contains(trackId))
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    sourcePath,
                    destinationPath,
                    RejectedTrackMoveStatus.ReviewConflict,
                    "同じ音源が別の重複レビューで残す側にも指定されています。"));
                continue;
            }

            if (track.IsMissing)
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    sourcePath,
                    destinationPath,
                    RejectedTrackMoveStatus.AlreadyMissing,
                    "音源は既に見つからない状態として記録されています。"));
                continue;
            }

            if (!fileOperations.FileExists(sourcePath))
            {
                items.Add(new RejectedTrackMoveItem(
                    trackId,
                    sourcePath,
                    destinationPath,
                    RejectedTrackMoveStatus.SourceMissing,
                    "移動元ファイルが存在しません。"));
                continue;
            }

            if (fileOperations.FileExists(destinationPath))
            {
                if (!execute || collisionBehavior == TrashDestinationCollisionBehavior.Skip)
                {
                    items.Add(new RejectedTrackMoveItem(
                        trackId,
                        sourcePath,
                        destinationPath,
                        RejectedTrackMoveStatus.DestinationExists,
                        "ごみ箱側に同じ絶対パス構造のファイルが既に存在します。"));
                    continue;
                }

                destinationPath = FindAvailableDestination(destinationPath);
            }

            if (!execute)
            {
                items.Add(new RejectedTrackMoveItem(trackId, sourcePath, destinationPath, RejectedTrackMoveStatus.Ready));
                continue;
            }

            try
            {
                fileOperations.Move(sourcePath, destinationPath);
                await trackRepository.DeleteFingerprintAsync(trackId, cancellationToken);
                await trackRepository.MarkMissingAsync(trackId, cancellationToken);
                items.Add(new RejectedTrackMoveItem(trackId, sourcePath, destinationPath, RejectedTrackMoveStatus.Moved));
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

    private string FindAvailableDestination(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("ごみ箱の移動先フォルダを解決できません。");
        var extension = Path.GetExtension(destinationPath);
        var name = Path.GetFileNameWithoutExtension(destinationPath);

        // (2)から開始し、既存Fileを上書きしない最小Available番号を採用する。
        for (var number = 2; number < int.MaxValue; number++)
        {
            var candidate = Path.Combine(directory, $"{name} ({number}){extension}");
            if (!fileOperations.FileExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("ごみ箱の移動先に使用できる別名を確保できません。");
    }

    private static long GetRejectTrackId(CandidateReview review)
        => review.KeepTrackId == review.Pair.TrackIdA ? review.Pair.TrackIdB : review.Pair.TrackIdA;
}
