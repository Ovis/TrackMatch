using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Trash;

/// <summary>
/// 確定済み重複グループのKeep以外を、安全条件を確認してApp-wide Trashへ移動する。
/// </summary>
public sealed class RejectedTrackTrashService
{
    private readonly ICandidateReviewRepository? _legacyReviewRepository;
    private readonly IDuplicateGroupRepository? _groupRepository;
    private readonly ITrackLookupRepository _trackLookupRepository;
    private readonly ITrackRepository _trackRepository;
    private readonly ITrackFileOperations _fileOperations;

    /// <summary>
    /// 重複グループを削除判断の正本として使用するコンストラクタ。
    /// </summary>
    public RejectedTrackTrashService(
        IDuplicateGroupRepository groupRepository,
        ITrackLookupRepository trackLookupRepository,
        ITrackRepository trackRepository,
        ITrackFileOperations fileOperations)
    {
        _groupRepository = groupRepository ?? throw new ArgumentNullException(nameof(groupRepository));
        _trackLookupRepository = trackLookupRepository ?? throw new ArgumentNullException(nameof(trackLookupRepository));
        _trackRepository = trackRepository ?? throw new ArgumentNullException(nameof(trackRepository));
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
    }

    /// <summary>
    /// 既存テストを移行するまでの互換コンストラクタ。
    /// </summary>
    [Obsolete("Global Duplicate Group経路を使用してください。")]
    public RejectedTrackTrashService(
        ICandidateReviewRepository reviewRepository,
        ITrackLookupRepository trackLookupRepository,
        ITrackRepository trackRepository,
        ITrackFileOperations fileOperations)
    {
        _legacyReviewRepository = reviewRepository ?? throw new ArgumentNullException(nameof(reviewRepository));
        _trackLookupRepository = trackLookupRepository ?? throw new ArgumentNullException(nameof(trackLookupRepository));
        _trackRepository = trackRepository ?? throw new ArgumentNullException(nameof(trackRepository));
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
    }

    /// <summary>
    /// 指定LibraryのDispositionでRejectとなったTrackをPreviewまたはTrashへ移動する。
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

        if (_groupRepository is not null)
        {
            return await ProcessGroupsAsync(
                libraryId,
                fullTrashRoot,
                execute,
                collisionBehavior,
                cancellationToken);
        }

        return await ProcessLegacyReviewsAsync(
            libraryId,
            fullTrashRoot,
            execute,
            collisionBehavior,
            cancellationToken);
    }

    private async Task<RejectedTrackTrashResult> ProcessGroupsAsync(
        long libraryId,
        string fullTrashRoot,
        bool execute,
        TrashDestinationCollisionBehavior collisionBehavior,
        CancellationToken cancellationToken)
    {
        var groups = await _groupRepository!.GetByLibraryIdAsync(libraryId, cancellationToken);
        var items = new List<RejectedTrackMoveItem>();

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            group.Validate();

            // Keepは現在Library外のGlobal Trackでも選択できる。Membershipではなく、物理ファイルが利用可能かだけを確認する。
            var keepTrack = await _trackLookupRepository.GetByIdAsync(group.KeepTrackId, cancellationToken);
            var keepIsAvailable = keepTrack is not null
                && !keepTrack.IsMissing
                && _fileOperations.FileExists(Path.GetFullPath(keepTrack.Metadata.Path));

            if (!keepIsAvailable)
            {
                foreach (var rejectTrackId in group.TrackIds.Where(trackId => trackId != group.KeepTrackId))
                {
                    if (!await _trackLookupRepository.IsInLibraryAsync(rejectTrackId, libraryId, cancellationToken))
                    {
                        continue;
                    }

                    var rejectTrack = await _trackLookupRepository.GetByIdAsync(rejectTrackId, cancellationToken);
                    if (rejectTrack is null)
                    {
                        continue;
                    }

                    var sourcePath = Path.GetFullPath(rejectTrack.Metadata.Path);
                    items.Add(new RejectedTrackMoveItem(
                        rejectTrackId,
                        sourcePath,
                        TrashPathRules.CreateDestinationPath(fullTrashRoot, sourcePath),
                        RejectedTrackMoveStatus.ReviewConflict,
                        "重複グループで残すファイルが利用できないため、安全のため移動を中止しました。"));
                }

                continue;
            }

            var rejectTrackIds = group.TrackIds.Where(trackId => trackId != group.KeepTrackId).ToArray();
            if (rejectTrackIds.Length != group.TrackIds.Count - 1)
            {
                throw new InvalidOperationException($"重複グループ #{group.Id} のKeep構成が不正なため、ごみ箱処理を拒否しました。");
            }

            foreach (var rejectTrackId in rejectTrackIds)
            {
                var item = await ProcessTrackAsync(
                    libraryId,
                    rejectTrackId,
                    fullTrashRoot,
                    execute,
                    collisionBehavior,
                    cancellationToken);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
        }

        return new RejectedTrackTrashResult(items, execute);
    }

    private async Task<RejectedTrackTrashResult> ProcessLegacyReviewsAsync(
        long libraryId,
        string fullTrashRoot,
        bool execute,
        TrashDestinationCollisionBehavior collisionBehavior,
        CancellationToken cancellationToken)
    {
        var reviews = await _legacyReviewRepository!.GetAllAsync(cancellationToken);
        var confirmed = reviews
            .Where(review => review.Decision == CandidateReviewDecision.ConfirmedDuplicate)
            .ToArray();
        var keepTrackIds = confirmed.Select(review => review.KeepTrackId!.Value).ToHashSet();
        var rejectTrackIds = confirmed.Select(GetRejectTrackId).Distinct().Order().ToArray();

        var items = new List<RejectedTrackMoveItem>(rejectTrackIds.Length);
        foreach (var trackId in rejectTrackIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _trackLookupRepository.IsInLibraryAsync(trackId, libraryId, cancellationToken))
            {
                continue;
            }

            var track = await _trackLookupRepository.GetByIdAsync(trackId, cancellationToken);
            if (track is null)
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

            items.Add(await ProcessKnownTrackAsync(
                track,
                sourcePath,
                destinationPath,
                execute,
                collisionBehavior,
                cancellationToken));
        }

        return new RejectedTrackTrashResult(items, execute);
    }

    private async Task<RejectedTrackMoveItem?> ProcessTrackAsync(
        long libraryId,
        long trackId,
        string fullTrashRoot,
        bool execute,
        TrashDestinationCollisionBehavior collisionBehavior,
        CancellationToken cancellationToken)
    {
        if (!await _trackLookupRepository.IsInLibraryAsync(trackId, libraryId, cancellationToken))
        {
            return null;
        }

        var track = await _trackLookupRepository.GetByIdAsync(trackId, cancellationToken);
        if (track is null)
        {
            return null;
        }

        var sourcePath = Path.GetFullPath(track.Metadata.Path);
        var destinationPath = TrashPathRules.CreateDestinationPath(fullTrashRoot, sourcePath);
        return await ProcessKnownTrackAsync(
            track,
            sourcePath,
            destinationPath,
            execute,
            collisionBehavior,
            cancellationToken);
    }

    private async Task<RejectedTrackMoveItem> ProcessKnownTrackAsync(
        StoredTrack track,
        string sourcePath,
        string destinationPath,
        bool execute,
        TrashDestinationCollisionBehavior collisionBehavior,
        CancellationToken cancellationToken)
    {
        if (track.IsMissing)
        {
            return new RejectedTrackMoveItem(
                track.Id,
                sourcePath,
                destinationPath,
                RejectedTrackMoveStatus.AlreadyMissing,
                "音源は既に見つからない状態として記録されています。");
        }

        if (!_fileOperations.FileExists(sourcePath))
        {
            return new RejectedTrackMoveItem(
                track.Id,
                sourcePath,
                destinationPath,
                RejectedTrackMoveStatus.SourceMissing,
                "移動元ファイルが存在しません。");
        }

        if (_fileOperations.FileExists(destinationPath))
        {
            if (!execute || collisionBehavior == TrashDestinationCollisionBehavior.Skip)
            {
                return new RejectedTrackMoveItem(
                    track.Id,
                    sourcePath,
                    destinationPath,
                    RejectedTrackMoveStatus.DestinationExists,
                    "ごみ箱側に同じ絶対パス構造のファイルが既に存在します。");
            }

            destinationPath = FindAvailableDestination(destinationPath);
        }

        if (!execute)
        {
            return new RejectedTrackMoveItem(track.Id, sourcePath, destinationPath, RejectedTrackMoveStatus.Ready);
        }

        try
        {
            _fileOperations.Move(sourcePath, destinationPath);
            try
            {
                // Trashは同じContentを別場所へ退避する操作なのでFingerprint等の解析キャッシュは保持する。
                // Global Trackは元Pathに存在しない状態としてMissingだけを更新する。
                await _trackRepository.MarkMissingAsync(track.Id, cancellationToken);
                return new RejectedTrackMoveItem(track.Id, sourcePath, destinationPath, RejectedTrackMoveStatus.Moved);
            }
            catch (Exception databaseException) when (databaseException is IOException or InvalidDataException or InvalidOperationException)
            {
                // FilesystemとSQLiteを同一Transactionにはできない。DB更新失敗時は元Pathへの補償Moveを試し、
                // 物理状態とDB状態が可能な限り分離しないようにする。
                try
                {
                    _fileOperations.Move(destinationPath, sourcePath);
                    return new RejectedTrackMoveItem(
                        track.Id,
                        sourcePath,
                        destinationPath,
                        RejectedTrackMoveStatus.Failed,
                        $"DB更新に失敗したためファイルを元の場所へ戻しました: {databaseException.Message}");
                }
                catch (Exception compensationException) when (compensationException is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return new RejectedTrackMoveItem(
                        track.Id,
                        sourcePath,
                        destinationPath,
                        RejectedTrackMoveStatus.Failed,
                        $"DB更新と補償Moveの両方に失敗しました。次回Scanで実状態を再確認してください。DB: {databaseException.Message} / 補償Move: {compensationException.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new RejectedTrackMoveItem(
                track.Id,
                sourcePath,
                destinationPath,
                RejectedTrackMoveStatus.Failed,
                exception.Message);
        }
    }

    private string FindAvailableDestination(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("ごみ箱の移動先フォルダを解決できません。");
        var extension = Path.GetExtension(destinationPath);
        var name = Path.GetFileNameWithoutExtension(destinationPath);

        for (var number = 2; number < int.MaxValue; number++)
        {
            var candidate = Path.Combine(directory, $"{name} ({number}){extension}");
            if (!_fileOperations.FileExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("ごみ箱の移動先に使用できる別名を確保できません。");
    }

    private static long GetRejectTrackId(CandidateReview review)
        => review.KeepTrackId == review.Pair.TrackIdA ? review.Pair.TrackIdB : review.Pair.TrackIdA;
}
