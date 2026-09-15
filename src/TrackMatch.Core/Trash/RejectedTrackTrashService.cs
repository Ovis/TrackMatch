using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Trash;

/// <summary>
/// Library固有Keepに基づき、重複グループのReject TrackをApp-wide Trashへ安全に移動する。
/// </summary>
public sealed class RejectedTrackTrashService(
    IDuplicateGroupRepository groupRepository,
    ITrackLookupRepository trackLookupRepository,
    ITrackRepository trackRepository,
    ITrackFileOperations fileOperations)
{
    /// <summary>
    /// 指定LibraryのDispositionでRejectとなったTrackをPreviewまたはTrashへ移動する。
    /// </summary>
    /// <remarks>
    /// TrackはGlobalな物理ファイルなので、他LibraryでもMembershipされている場合は結果へ影響情報を含める。
    /// Keepが未選択・競合・MissingのGroupは安全側で移動を拒否する。
    /// </remarks>
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
        var groups = await groupRepository.GetByLibraryIdAsync(libraryId, cancellationToken);
        var items = new List<RejectedTrackMoveItem>();
        var impacts = new Dictionary<long, SharedTrackTrashImpact>();

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            group.Validate();

            if (group.KeepStatus != DuplicateGroupKeepStatus.Selected || group.KeepTrackId is null)
            {
                await AddBlockedGroupItemsAsync(
                    group,
                    libraryId,
                    fullTrashRoot,
                    items,
                    "重複グループで残すファイルが未確定のため、安全のため移動を中止しました。",
                    cancellationToken);
                continue;
            }

            var keepTrackId = group.KeepTrackId.Value;
            var keepTrack = await trackLookupRepository.GetByIdAsync(keepTrackId, cancellationToken);
            var keepIsAvailable = keepTrack is not null
                && !keepTrack.IsMissing
                && fileOperations.FileExists(Path.GetFullPath(keepTrack.Metadata.Path));
            if (!keepIsAvailable)
            {
                await AddBlockedGroupItemsAsync(
                    group,
                    libraryId,
                    fullTrashRoot,
                    items,
                    "重複グループで残すファイルが利用できないため、安全のため移動を中止しました。",
                    cancellationToken);
                continue;
            }

            // KeepがLibrary外Trackの場合、現在Libraryの構成TrackはすべてReject候補になる。
            var rejectTrackIds = group.TrackIds
                .Where(trackId => trackId != keepTrackId)
                .Distinct()
                .ToArray();

            foreach (var rejectTrackId in rejectTrackIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var impact = await CreateSharedImpactAsync(libraryId, rejectTrackId, cancellationToken);
                if (impact.IsShared)
                {
                    impacts[rejectTrackId] = impact;
                }

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

        return new RejectedTrackTrashResult(items, execute, impacts.Values.OrderBy(item => item.TrackId).ToArray());
    }

    /// <summary>
    /// 現在Library外にあるTrack 1件を、ユーザーの明示操作としてPreviewまたはTrashへ移動する。
    /// </summary>
    /// <remarks>
    /// Library単位の一括Trashとは別の明示操作用APIである。
    /// 現在Library所属Trackと現在LibraryのKeep TrackはLibrary固有Dispositionを考慮する必要があるため、このAPIでは拒否する。
    /// これによりUIのボタン制御に依存せず、Library外のReject候補だけを明示的なGlobal物理操作の対象にする。
    /// </remarks>
    /// <param name="currentLibraryId">操作元画面のLibrary。対象がLibrary外であることと他Libraryへの影響判定に使用する</param>
    /// <param name="trackId">明示的にTrash対象として選択されたGlobal Track</param>
    /// <param name="trashRoot">App-wide Trashのルート</param>
    /// <param name="execute">falseはPreview、trueは実移動</param>
    /// <param name="collisionBehavior">移動先衝突時の処理</param>
    /// <param name="cancellationToken">キャンセルToken</param>
    public async Task<RejectedTrackTrashResult> ProcessTrackGloballyAsync(
        long currentLibraryId,
        long trackId,
        string trashRoot,
        bool execute,
        TrashDestinationCollisionBehavior collisionBehavior = TrashDestinationCollisionBehavior.Skip,
        CancellationToken cancellationToken = default)
    {
        if (currentLibraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentLibraryId));
        }

        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(trashRoot);
        var fullTrashRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trashRoot));
        var track = await trackLookupRepository.GetByIdAsync(trackId, cancellationToken);
        if (track is null)
        {
            return new RejectedTrackTrashResult([], execute, []);
        }

        // 現在Library所属Trackをこの経路で処理すると、そのLibrary自身のKeep影響を見落としたまま
        // 物理ファイルを移動できてしまう。Library内TrackはKeepを考慮する一括Trash経路へ限定する。
        if (await trackLookupRepository.IsInLibraryAsync(trackId, currentLibraryId, cancellationToken))
        {
            throw new InvalidOperationException(
                "現在のライブラリに所属するファイルは、ライブラリ外ファイル用の明示的なごみ箱操作では移動できません。");
        }

        // KeepはLibrary外Trackを指すこともできる。Membershipだけで判定すると、現在Libraryが残すよう指定した
        // 外部ファイルをこのGlobal操作で消せるため、Projection上のKeepも独立してGuardする。
        var currentGroup = await groupRepository.GetByTrackIdAsync(trackId, currentLibraryId, cancellationToken);
        if (currentGroup?.KeepStatus == DuplicateGroupKeepStatus.Selected
            && currentGroup.KeepTrackId == trackId)
        {
            throw new InvalidOperationException(
                "このファイルは現在のライブラリで残すファイルに指定されているため、ごみ箱へ移動できません。");
        }

        var sourcePath = Path.GetFullPath(track.Metadata.Path);
        var destinationPath = TrashPathRules.CreateDestinationPath(fullTrashRoot, sourcePath);
        var impact = await CreateSharedImpactAsync(currentLibraryId, trackId, cancellationToken);
        var item = await ProcessKnownTrackAsync(
            track,
            sourcePath,
            destinationPath,
            execute,
            collisionBehavior,
            cancellationToken);

        return new RejectedTrackTrashResult(
            [item],
            execute,
            impact.IsShared ? [impact] : []);
    }

    private async Task AddBlockedGroupItemsAsync(
        DuplicateGroup group,
        long libraryId,
        string fullTrashRoot,
        ICollection<RejectedTrackMoveItem> items,
        string message,
        CancellationToken cancellationToken)
    {
        foreach (var trackId in group.TrackIds)
        {
            if (!await trackLookupRepository.IsInLibraryAsync(trackId, libraryId, cancellationToken))
            {
                continue;
            }

            var track = await trackLookupRepository.GetByIdAsync(trackId, cancellationToken);
            if (track is null)
            {
                continue;
            }

            var sourcePath = Path.GetFullPath(track.Metadata.Path);
            items.Add(new RejectedTrackMoveItem(
                trackId,
                sourcePath,
                TrashPathRules.CreateDestinationPath(fullTrashRoot, sourcePath),
                RejectedTrackMoveStatus.ReviewConflict,
                message));
        }
    }

    private async Task<SharedTrackTrashImpact> CreateSharedImpactAsync(
        long currentLibraryId,
        long trackId,
        CancellationToken cancellationToken)
    {
        var libraries = await trackLookupRepository.GetLibrariesAsync(trackId, cancellationToken);
        var otherLibraries = libraries.Where(library => library.Id != currentLibraryId).ToArray();
        if (otherLibraries.Length == 0)
        {
            return new SharedTrackTrashImpact(trackId, [], []);
        }

        var keepLibraries = new List<TrackLibraryReference>();
        foreach (var library in otherLibraries)
        {
            var group = await groupRepository.GetByTrackIdAsync(trackId, library.Id, cancellationToken);
            if (group?.KeepStatus == DuplicateGroupKeepStatus.Selected && group.KeepTrackId == trackId)
            {
                keepLibraries.Add(library);
            }
        }

        return new SharedTrackTrashImpact(trackId, otherLibraries, keepLibraries);
    }

    private async Task<RejectedTrackMoveItem?> ProcessTrackAsync(
        long libraryId,
        long trackId,
        string fullTrashRoot,
        bool execute,
        TrashDestinationCollisionBehavior collisionBehavior,
        CancellationToken cancellationToken)
    {
        if (!await trackLookupRepository.IsInLibraryAsync(trackId, libraryId, cancellationToken))
        {
            return null;
        }

        var track = await trackLookupRepository.GetByIdAsync(trackId, cancellationToken);
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

        if (!fileOperations.FileExists(sourcePath))
        {
            return new RejectedTrackMoveItem(
                track.Id,
                sourcePath,
                destinationPath,
                RejectedTrackMoveStatus.SourceMissing,
                "移動元ファイルが存在しません。");
        }

        if (fileOperations.FileExists(destinationPath))
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
            fileOperations.Move(sourcePath, destinationPath);
            try
            {
                // 物理Moveが成功した時点からはDBとの整合性確定を優先する。
                // ユーザーCancelをここへ伝播すると「ファイルだけ移動済み」の状態を作るため、Missing更新はキャンセル不可で完了させる。
                await trackRepository.MarkMissingAsync(track.Id, CancellationToken.None);
                return new RejectedTrackMoveItem(track.Id, sourcePath, destinationPath, RejectedTrackMoveStatus.Moved);
            }
            catch (Exception databaseException)
            {
                // FilesystemとSQLiteを同一Transactionにはできないため、DB確定に失敗した場合は例外種別を問わず補償Moveを試す。
                // 補償中もCancelを理由に中断せず、元PathとDB状態の一致を最優先する。
                try
                {
                    fileOperations.Move(destinationPath, sourcePath);
                    return new RejectedTrackMoveItem(
                        track.Id,
                        sourcePath,
                        destinationPath,
                        RejectedTrackMoveStatus.Failed,
                        $"DB更新に失敗したためファイルを元の場所へ戻しました: {databaseException.Message}");
                }
                catch (Exception compensationException)
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
            if (!fileOperations.FileExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("ごみ箱の移動先に使用できる別名を確保できません。");
    }
}
