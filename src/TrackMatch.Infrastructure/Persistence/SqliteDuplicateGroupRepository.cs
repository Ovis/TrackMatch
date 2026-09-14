using Dapper;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Global Duplicate GroupとLibrary固有Keep StateをSQLiteへ永続化する。
/// </summary>
public sealed class SqliteDuplicateGroupRepository(SqliteDatabase database) : IDuplicateGroupRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<GlobalDuplicateGroup>> GetAllGlobalAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await LoadGlobalGroupsAsync(connection, transaction: null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DuplicateGroup>> GetByLibraryIdAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var globalGroups = await LoadGlobalGroupsAsync(connection, transaction: null, cancellationToken);
        var memberTrackIds = (await connection.QueryAsync<long>(new CommandDefinition(
            """
            SELECT lt.TrackId
            FROM LibraryTracks lt
            INNER JOIN Tracks t ON t.Id = lt.TrackId
            WHERE lt.LibraryId = @LibraryId AND t.IsMissing = 0;
            """,
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken))).ToHashSet();
        var keepStates = await LoadKeepStatesAsync(connection, libraryId, transaction: null, cancellationToken);
        var missingTrackIds = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT Id FROM Tracks WHERE IsMissing = 1;",
            cancellationToken: cancellationToken))).ToHashSet();

        return globalGroups
            .Where(group => group.TrackIds.Any(memberTrackIds.Contains))
            .Select(group => CreateProjection(group, libraryId, memberTrackIds, keepStates.GetValueOrDefault(group.Id), missingTrackIds))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<DuplicateGroup?> GetByTrackIdAsync(
        long trackId,
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        var groups = await GetByLibraryIdAsync(libraryId, cancellationToken);
        return groups.SingleOrDefault(group => group.GlobalTrackIds.Contains(trackId));
    }

    /// <inheritdoc />
    public async Task<DuplicateGroup?> GetByIdAsync(
        long groupId,
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        if (groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupId));
        }

        var groups = await GetByLibraryIdAsync(libraryId, cancellationToken);
        return groups.SingleOrDefault(group => group.Id == groupId);
    }

    /// <inheritdoc />
    public async Task ReplaceGlobalAsync(
        IReadOnlyCollection<DuplicateGroupRebuildItem> groups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ValidateRebuild(groups);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var oldGroups = await LoadGlobalGroupsAsync(connection, transaction, cancellationToken);
        var oldKeepStates = (await connection.QueryAsync<KeepStateRow>(new CommandDefinition(
            "SELECT LibraryId, DuplicateGroupId, KeepTrackId, Status, UpdatedAtUtcTicks FROM LibraryDuplicateGroupKeepStates;",
            transaction: transaction,
            cancellationToken: cancellationToken))).ToArray();

        var oldById = oldGroups.ToDictionary(group => group.Id);
        var requestedExistingIds = groups
            .Where(group => group.ExistingGroupId is not null)
            .Select(group => group.ExistingGroupId!.Value)
            .ToHashSet();
        if (requestedExistingIds.Any(id => !oldById.ContainsKey(id)))
        {
            throw new InvalidOperationException("再利用対象のGlobal Duplicate Group IDが現行構成に存在しません。");
        }

        // Track集合まで完全一致するGroupはCurrent Stateそのものに変化がない。
        // 他GroupのSplit/Mergeへ巻き込んでKeep HistoryやUpdatedAtを更新しないよう、そのまま保持する。
        var unchangedGroupIds = groups
            .Where(plan => plan.ExistingGroupId is { } id
                && oldById.TryGetValue(id, out var oldGroup)
                && SameTrackSet(oldGroup.TrackIds, plan.TrackIds))
            .Select(plan => plan.ExistingGroupId!.Value)
            .ToHashSet();
        var changedOldGroupIds = oldGroups
            .Select(group => group.Id)
            .Where(id => !unchangedGroupIds.Contains(id))
            .ToHashSet();
        var changedPlans = groups
            .Where(plan => plan.ExistingGroupId is not { } id || !unchangedGroupIds.Contains(id))
            .ToArray();
        var changedOldKeepStates = oldKeepStates
            .Where(state => changedOldGroupIds.Contains(state.DuplicateGroupId))
            .ToArray();
        var selectedStatesAffectedByTrackRestore = new HashSet<(long LibraryId, long DuplicateGroupId)>();

        // 実際にTopologyが変わるGroupだけを履歴化する。
        // 無関係なGroupをGroupRebuildとして記録すると、監査履歴とUpdatedAtの意味が失われるため触らない。
        foreach (var state in changedOldKeepStates)
        {
            var oldGroup = oldById.GetValueOrDefault(state.DuplicateGroupId);
            if (oldGroup is null)
            {
                continue;
            }

            var changeKind = await ResolveRebuildChangeKindAsync(
                connection,
                transaction,
                oldGroup,
                state,
                oldGroups,
                groups,
                cancellationToken);
            if (string.Equals(changeKind, "TrackRestored", StringComparison.Ordinal)
                && string.Equals(state.Status, nameof(DuplicateGroupKeepStatus.Selected), StringComparison.Ordinal))
            {
                // Trash/Missingから構成Trackが戻ったGroupでは旧Dispositionをそのまま再適用しない。
                // Historyには実際のSelected状態をTrackRestoredとして残し、Current再構築時だけ要確認扱いへ落とす。
                selectedStatesAffectedByTrackRestore.Add((state.LibraryId, state.DuplicateGroupId));
            }

            await InsertKeepHistoryAsync(
                connection,
                transaction,
                state.LibraryId,
                state.DuplicateGroupId,
                BuildGraphKey(oldGroup.TrackIds),
                state.KeepTrackId,
                state.Status,
                changeKind,
                note: null,
                cancellationToken);
        }

        if (changedOldGroupIds.Count != 0)
        {
            // 変更対象だけ構成行とKeep Current Stateを外す。TrackIdのGlobal UNIQUE制約との一時衝突も、
            // Split/Mergeへ関与する旧Groupをまとめて外すことで回避する。
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM DuplicateGroupTracks WHERE DuplicateGroupId IN @GroupIds;",
                new { GroupIds = changedOldGroupIds.ToArray() },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LibraryDuplicateGroupKeepStates WHERE DuplicateGroupId IN @GroupIds;",
                new { GroupIds = changedOldGroupIds.ToArray() },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var obsoleteId in changedOldGroupIds.Where(id => !requestedExistingIds.Contains(id)))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM DuplicateGroups WHERE Id = @Id;",
                new { Id = obsoleteId },
                transaction,
                cancellationToken: cancellationToken));
        }

        var persistedChangedGroups = new List<(long Id, DuplicateGroupRebuildItem Plan)>(changedPlans.Length);
        foreach (var plan in changedPlans)
        {
            var graphKey = BuildGraphKey(plan.TrackIds);
            long groupId;
            if (plan.ExistingGroupId is { } existingId)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE DuplicateGroups SET GraphKey = @GraphKey WHERE Id = @Id;",
                    new { Id = existingId, GraphKey = graphKey },
                    transaction,
                    cancellationToken: cancellationToken));
                groupId = existingId;
            }
            else
            {
                groupId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    INSERT INTO DuplicateGroups (GraphKey) VALUES (@GraphKey);
                    SELECT last_insert_rowid();
                    """,
                    new { GraphKey = graphKey },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO DuplicateGroupTracks (DuplicateGroupId, TrackId) VALUES (@DuplicateGroupId, @TrackId);",
                plan.TrackIds.Select(trackId => new { DuplicateGroupId = groupId, TrackId = trackId }),
                transaction,
                cancellationToken: cancellationToken));
            persistedChangedGroups.Add((groupId, plan));
        }

        // TrackRestoredになったSelected Stateは、既存のMissing復帰ルールをCurrent再構築にも適用する。
        // Keep IDそのものがMissingだった場合だけでなく、Reject側Trackの復帰でも旧Trash判断を自動再適用しないためである。
        var restoreSourceStates = changedOldKeepStates
            .Select(state => selectedStatesAffectedByTrackRestore.Contains((state.LibraryId, state.DuplicateGroupId))
                ? state with { Status = nameof(DuplicateGroupKeepStatus.Missing) }
                : state)
            .ToArray();
        await RestoreKeepStatesAsync(
            connection,
            transaction,
            oldGroups,
            restoreSourceStates,
            persistedChangedGroups,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetKeepAsync(
        long libraryId,
        long groupId,
        long keepTrackId,
        string changeKind,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        if (groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupId));
        }

        if (keepTrackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepTrackId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(changeKind);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var globalTrackIds = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT TrackId FROM DuplicateGroupTracks WHERE DuplicateGroupId = @GroupId ORDER BY TrackId;",
            new { GroupId = groupId },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        if (globalTrackIds.Length < 2 || !globalTrackIds.Contains(keepTrackId))
        {
            throw new InvalidOperationException("Keep Trackは指定Global Duplicate Groupの構成Trackである必要があります。");
        }

        var libraryExists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Libraries WHERE Id = @LibraryId;",
            new { LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken));
        if (libraryExists == 0)
        {
            throw new InvalidOperationException("Keepを設定するLibraryが存在しません。");
        }

        // Library外TrackをKeepにすること自体は許可するが、そのLibraryがGroupへ全く関与していない状態では
        // Library固有Dispositionを作成できない。Projectionと同じく、少なくとも1件のActive Membershipを要求する。
        var libraryHasGroupMembership = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM DuplicateGroupTracks gt
            INNER JOIN LibraryTracks lt ON lt.TrackId = gt.TrackId
            INNER JOIN Tracks t ON t.Id = lt.TrackId AND t.IsMissing = 0
            WHERE gt.DuplicateGroupId = @GroupId
              AND lt.LibraryId = @LibraryId;
            """,
            new { LibraryId = libraryId, GroupId = groupId },
            transaction,
            cancellationToken: cancellationToken));
        if (libraryHasGroupMembership == 0)
        {
            throw new InvalidOperationException("現在LibraryがActive Membershipを持たないGlobal Duplicate GroupへKeepを設定できません。");
        }

        var current = await connection.QuerySingleOrDefaultAsync<KeepStateRow>(new CommandDefinition(
            """
            SELECT LibraryId, DuplicateGroupId, KeepTrackId, Status, UpdatedAtUtcTicks
            FROM LibraryDuplicateGroupKeepStates
            WHERE LibraryId = @LibraryId AND DuplicateGroupId = @GroupId;
            """,
            new { LibraryId = libraryId, GroupId = groupId },
            transaction,
            cancellationToken: cancellationToken));
        if (current is not null
            && current.KeepTrackId == keepTrackId
            && string.Equals(current.Status, nameof(DuplicateGroupKeepStatus.Selected), StringComparison.Ordinal))
        {
            // 同じSelected Keepへの再設定は状態遷移ではないため、HistoryもUpdatedAtも変更しない。
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (current is not null)
        {
            await InsertKeepHistoryAsync(
                connection,
                transaction,
                libraryId,
                groupId,
                BuildGraphKey(globalTrackIds),
                current.KeepTrackId,
                current.Status,
                changeKind,
                note: null,
                cancellationToken);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO LibraryDuplicateGroupKeepStates (
                LibraryId, DuplicateGroupId, KeepTrackId, Status, UpdatedAtUtcTicks)
            VALUES (@LibraryId, @GroupId, @KeepTrackId, 'Selected', @UpdatedAtUtcTicks)
            ON CONFLICT(LibraryId, DuplicateGroupId) DO UPDATE SET
                KeepTrackId = excluded.KeepTrackId,
                Status = excluded.Status,
                UpdatedAtUtcTicks = excluded.UpdatedAtUtcTicks;
            """,
            new
            {
                LibraryId = libraryId,
                GroupId = groupId,
                KeepTrackId = keepTrackId,
                UpdatedAtUtcTicks = DateTime.UtcNow.Ticks,
            },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RestoreKeepStatesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlyList<GlobalDuplicateGroup> oldGroups,
        IReadOnlyList<KeepStateRow> oldStates,
        IReadOnlyList<(long Id, DuplicateGroupRebuildItem Plan)> newGroups,
        CancellationToken cancellationToken)
    {
        var libraryIds = oldStates.Select(state => state.LibraryId).Distinct().ToArray();

        foreach (var newGroup in newGroups)
        {
            var newTrackSet = newGroup.Plan.TrackIds.ToHashSet();
            var overlappingOldIds = oldGroups
                .Where(group => group.TrackIds.Any(newTrackSet.Contains))
                .Select(group => group.Id)
                .ToHashSet();

            foreach (var libraryId in libraryIds)
            {
                var relevantStates = oldStates
                    .Where(state => state.LibraryId == libraryId && overlappingOldIds.Contains(state.DuplicateGroupId))
                    .ToArray();
                if (relevantStates.Length == 0)
                {
                    continue;
                }

                // Split後にこのLibraryのActive Membershipを1件も含まないComponentへCurrent Keepを持ち越さない。
                // 過去判断はHistoryへ残るため、後からMembershipが追加されても古いKeepが突然復活することはない。
                var libraryHasMembership = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    SELECT COUNT(*)
                    FROM LibraryTracks lt
                    INNER JOIN Tracks t ON t.Id = lt.TrackId AND t.IsMissing = 0
                    WHERE lt.LibraryId = @LibraryId
                      AND lt.TrackId IN @TrackIds;
                    """,
                    new { LibraryId = libraryId, TrackIds = newTrackSet.ToArray() },
                    transaction,
                    cancellationToken: cancellationToken));
                if (libraryHasMembership == 0)
                {
                    continue;
                }

                var priorKeepStates = relevantStates
                    .Where(state => state.KeepTrackId is not null
                        && (string.Equals(state.Status, nameof(DuplicateGroupKeepStatus.Selected), StringComparison.Ordinal)
                            || string.Equals(state.Status, nameof(DuplicateGroupKeepStatus.Missing), StringComparison.Ordinal)))
                    .ToArray();
                var priorKeepIds = priorKeepStates
                    .Select(state => state.KeepTrackId!.Value)
                    .Distinct()
                    .ToArray();

                long? keepTrackId = null;
                var status = DuplicateGroupKeepStatus.Unselected;
                if (relevantStates.Any(state => string.Equals(state.Status, nameof(DuplicateGroupKeepStatus.Conflict), StringComparison.Ordinal))
                    || priorKeepIds.Length > 1)
                {
                    // Mergeで異なるKeepが集まった場合は自動選択せず再確認を要求する。
                    status = DuplicateGroupKeepStatus.Conflict;
                }
                else if (priorKeepIds.Length == 1)
                {
                    var priorKeepId = priorKeepIds[0];
                    var trackState = await connection.QuerySingleOrDefaultAsync<TrackStateRow>(new CommandDefinition(
                        "SELECT Id, IsMissing FROM Tracks WHERE Id = @TrackId;",
                        new { TrackId = priorKeepId },
                        transaction,
                        cancellationToken: cancellationToken));

                    if (trackState is not null && trackState.IsMissing != 0)
                    {
                        keepTrackId = priorKeepId;
                        status = DuplicateGroupKeepStatus.Missing;
                    }
                    else if (trackState is not null
                        && newTrackSet.Contains(priorKeepId)
                        && priorKeepStates.All(state => !string.Equals(
                            state.Status,
                            nameof(DuplicateGroupKeepStatus.Missing),
                            StringComparison.Ordinal)))
                    {
                        // 通常のMerge/Splitでは有効なKeepを引き継ぐ。一方、Missingから復帰したTrackは
                        // 以前のDispositionを自動復元せず、Current Groupを要確認へ戻す。
                        keepTrackId = priorKeepId;
                        status = DuplicateGroupKeepStatus.Selected;
                    }
                    // Missing復帰時またはSplit後にKeepが別成分へ移った場合はUnselectedのままにする。
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO LibraryDuplicateGroupKeepStates (
                        LibraryId, DuplicateGroupId, KeepTrackId, Status, UpdatedAtUtcTicks)
                    VALUES (@LibraryId, @GroupId, @KeepTrackId, @Status, @UpdatedAtUtcTicks);
                    """,
                    new
                    {
                        LibraryId = libraryId,
                        GroupId = newGroup.Id,
                        KeepTrackId = keepTrackId,
                        Status = status.ToString(),
                        UpdatedAtUtcTicks = DateTime.UtcNow.Ticks,
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }
    }

    private static DuplicateGroup CreateProjection(
        GlobalDuplicateGroup globalGroup,
        long libraryId,
        IReadOnlySet<long> memberTrackIds,
        KeepStateRow? keepState,
        IReadOnlySet<long> missingTrackIds)
    {
        var projectionTrackIds = globalGroup.TrackIds.Where(memberTrackIds.Contains).ToArray();
        var status = DuplicateGroupKeepStatus.Unselected;
        long? keepTrackId = null;

        if (keepState is not null && Enum.TryParse<DuplicateGroupKeepStatus>(keepState.Status, out var parsedStatus))
        {
            status = parsedStatus;
            if (status == DuplicateGroupKeepStatus.Selected && keepState.KeepTrackId is { } selected)
            {
                if (missingTrackIds.Contains(selected))
                {
                    status = DuplicateGroupKeepStatus.Missing;
                }
                else
                {
                    keepTrackId = selected;
                }
            }
        }

        var group = new DuplicateGroup(
            globalGroup.Id,
            libraryId,
            keepTrackId,
            status,
            projectionTrackIds,
            globalGroup.TrackIds);
        group.Validate();
        return group;
    }

    private static async Task<IReadOnlyList<GlobalDuplicateGroup>> LoadGlobalGroupsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var groups = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT Id FROM DuplicateGroups ORDER BY Id;",
            transaction: transaction,
            cancellationToken: cancellationToken))).ToArray();
        if (groups.Length == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<GroupTrackRow>(new CommandDefinition(
            "SELECT DuplicateGroupId, TrackId FROM DuplicateGroupTracks ORDER BY DuplicateGroupId, TrackId;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        var byGroup = rows.GroupBy(row => row.DuplicateGroupId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<long>)group.Select(row => row.TrackId).ToArray());
        return groups
            .Select(id => new GlobalDuplicateGroup(id, byGroup.GetValueOrDefault(id) ?? []))
            .Where(group => group.TrackIds.Count >= 2)
            .ToArray();
    }

    private static async Task<Dictionary<long, KeepStateRow>> LoadKeepStatesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        long libraryId,
        System.Data.Common.DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<KeepStateRow>(new CommandDefinition(
            """
            SELECT LibraryId, DuplicateGroupId, KeepTrackId, Status, UpdatedAtUtcTicks
            FROM LibraryDuplicateGroupKeepStates
            WHERE LibraryId = @LibraryId;
            """,
            new { LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken));
        return rows.ToDictionary(row => row.DuplicateGroupId);
    }

    private static async Task<string> ResolveRebuildChangeKindAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        GlobalDuplicateGroup oldGroup,
        KeepStateRow state,
        IReadOnlyList<GlobalDuplicateGroup> oldGroups,
        IReadOnlyCollection<DuplicateGroupRebuildItem> newGroups,
        CancellationToken cancellationToken)
    {
        var overlappingNewGroups = newGroups
            .Where(group => group.TrackIds.Any(oldGroup.TrackIds.Contains))
            .ToArray();

        if (state.KeepTrackId is { } keepTrackId)
        {
            var trackState = await connection.QuerySingleOrDefaultAsync<TrackStateRow>(new CommandDefinition(
                "SELECT Id, IsMissing FROM Tracks WHERE Id = @TrackId;",
                new { TrackId = keepTrackId },
                transaction,
                cancellationToken: cancellationToken));
            if (trackState is not null)
            {
                if (string.Equals(state.Status, nameof(DuplicateGroupKeepStatus.Selected), StringComparison.Ordinal)
                    && trackState.IsMissing != 0)
                {
                    return "KeepMissing";
                }

                if (string.Equals(state.Status, nameof(DuplicateGroupKeepStatus.Missing), StringComparison.Ordinal)
                    && trackState.IsMissing == 0
                    && overlappingNewGroups.Any(group => group.TrackIds.Contains(keepTrackId)))
                {
                    return "KeepRestored";
                }
            }
        }

        var overlappingNewTrackIds = overlappingNewGroups
            .SelectMany(group => group.TrackIds)
            .ToHashSet();
        var removedTrackIds = oldGroup.TrackIds
            .Where(trackId => !overlappingNewTrackIds.Contains(trackId))
            .ToArray();
        if (removedTrackIds.Length != 0)
        {
            var missingCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM Tracks WHERE Id IN @TrackIds AND IsMissing = 1;",
                new { TrackIds = removedTrackIds },
                transaction,
                cancellationToken: cancellationToken));
            if (missingCount != 0)
            {
                return "TrackMissing";
            }
        }

        if (overlappingNewGroups.Length == 1)
        {
            var newTrackIds = overlappingNewGroups[0].TrackIds.ToHashSet();
            var addedTrackIds = newTrackIds.Where(trackId => !oldGroup.TrackIds.Contains(trackId)).ToArray();
            if (addedTrackIds.Length != 0)
            {
                // Group IDや現在のGraph形状はMissing中のSplit/Merge/Verdict変更で変化し得る。
                // 復帰判定には追加Trackを含む直近Topology履歴を使うが、Missing中にそのTrackのHuman Verdictを
                // 解除・変更した場合は、古い物理Missingより新しいユーザー判断を優先して通常のGroup変更として扱う。
                foreach (var addedTrackId in addedTrackIds)
                {
                    var latestTransition = await connection.QuerySingleOrDefaultAsync<KeepHistoryTransitionRow>(new CommandDefinition(
                        """
                        SELECT ChangeKind, GraphKeySnapshot, ChangedAtUtcTicks
                        FROM LibraryDuplicateGroupKeepHistory
                        WHERE LibraryId = @LibraryId
                          AND ChangeKind IN (
                              'TrackMissing', 'TrackRestored',
                              'KeepMissing', 'KeepRestored',
                              'GroupSplit', 'GroupMerge', 'GroupRebuild',
                              'ScopeRemoved', 'LibraryDeleted')
                          AND (',' || GraphKeySnapshot || ',') LIKE @TrackToken
                        ORDER BY Id DESC
                        LIMIT 1;
                        """,
                        new
                        {
                            LibraryId = state.LibraryId,
                            TrackToken = $"%,{addedTrackId},%",
                        },
                        transaction,
                        cancellationToken: cancellationToken));
                    if (latestTransition is null
                        || (!string.Equals(latestTransition.ChangeKind, "TrackMissing", StringComparison.Ordinal)
                            && !string.Equals(latestTransition.ChangeKind, "KeepMissing", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var humanVerdictChangedAfterMissing = await HasHumanVerdictMutationAfterAsync(
                        connection,
                        transaction,
                        addedTrackId,
                        latestTransition.ChangedAtUtcTicks,
                        cancellationToken);
                    if (!humanVerdictChangedAfterMissing)
                    {
                        // MissingだったTrack自身が旧Keepだった場合、Missing中に代替Keepを選び直しても
                        // Global Verdictが変わっていなければ物理復帰として扱う。Q64に従い旧Dispositionは自動適用しない。
                        return "TrackRestored";
                    }
                }
            }
        }

        if (overlappingNewGroups.Length > 1)
        {
            return "GroupSplit";
        }

        if (overlappingNewGroups.Length == 1)
        {
            var newTrackSet = overlappingNewGroups[0].TrackIds.ToHashSet();
            var overlappingOldCount = oldGroups.Count(group => group.TrackIds.Any(newTrackSet.Contains));
            if (overlappingOldCount > 1)
            {
                return "GroupMerge";
            }
        }

        return "GroupRebuild";
    }

    private static async Task<bool> HasHumanVerdictMutationAfterAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        long trackId,
        long transitionAtUtcTicks,
        CancellationToken cancellationToken)
    {
        // Missing期間中に対象Trackを含むVerdictを解除・変更した場合、後日の再追加はユーザーの新しい判断である。
        // Keep Historyだけから物理Restoreと推定すると古いTrackMissingを再利用してしまうため、Verdict時系列も境界に含める。
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM (
                SELECT ReviewedAtUtcTicks AS ChangedAtUtcTicks
                FROM CandidateReviews
                WHERE (TrackIdA = @TrackId OR TrackIdB = @TrackId)
                  AND ReviewedAtUtcTicks > @TransitionAtUtcTicks
                UNION ALL
                SELECT ChangedAtUtcTicks
                FROM CandidateReviewHistory
                WHERE (TrackIdA = @TrackId OR TrackIdB = @TrackId)
                  AND ChangedAtUtcTicks > @TransitionAtUtcTicks
            );
            """,
            new { TrackId = trackId, TransitionAtUtcTicks = transitionAtUtcTicks },
            transaction,
            cancellationToken: cancellationToken));
        return count != 0;
    }

    private static async Task InsertKeepHistoryAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        long libraryId,
        long? groupId,
        string graphKey,
        long? keepTrackId,
        string status,
        string changeKind,
        string? note,
        CancellationToken cancellationToken)
    {
        var libraryName = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT Name FROM Libraries WHERE Id = @LibraryId;",
            new { LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO LibraryDuplicateGroupKeepHistory (
                LibraryId, LibraryNameSnapshot, DuplicateGroupId, GraphKeySnapshot,
                KeepTrackId, Status, ChangeKind, ChangedAtUtcTicks, Note)
            VALUES (
                @LibraryId, @LibraryNameSnapshot, @DuplicateGroupId, @GraphKeySnapshot,
                @KeepTrackId, @Status, @ChangeKind, @ChangedAtUtcTicks, @Note);
            """,
            new
            {
                LibraryId = libraryName is null ? (long?)null : libraryId,
                LibraryNameSnapshot = libraryName,
                DuplicateGroupId = groupId,
                GraphKeySnapshot = graphKey,
                KeepTrackId = keepTrackId,
                Status = status,
                ChangeKind = changeKind,
                ChangedAtUtcTicks = DateTime.UtcNow.Ticks,
                Note = note,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static bool SameTrackSet(IReadOnlyList<long> left, IReadOnlyList<long> right)
        => left.Count == right.Count && left.Order().SequenceEqual(right.Order());

    private static string BuildGraphKey(IEnumerable<long> trackIds)
        => string.Join(',', trackIds.Order());

    private static void ValidateRebuild(IReadOnlyCollection<DuplicateGroupRebuildItem> groups)
    {
        var allTrackIds = new HashSet<long>();
        var existingIds = new HashSet<long>();
        foreach (var group in groups)
        {
            if (group.TrackIds.Count < 2
                || group.TrackIds.Any(trackId => trackId <= 0)
                || group.TrackIds.Distinct().Count() != group.TrackIds.Count)
            {
                throw new ArgumentException("Global Duplicate Groupには重複のない2件以上のTrackが必要です。", nameof(groups));
            }

            if (group.TrackIds.Any(trackId => !allTrackIds.Add(trackId)))
            {
                throw new ArgumentException("1つのTrackを複数のGlobal Duplicate Groupへ所属させることはできません。", nameof(groups));
            }

            if (group.ExistingGroupId is { } existingId && !existingIds.Add(existingId))
            {
                throw new ArgumentException("同じ既存Global Group IDを複数成分へ再利用できません。", nameof(groups));
            }
        }
    }

    private sealed record GroupTrackRow(long DuplicateGroupId, long TrackId);
    private sealed record TrackStateRow(long Id, long IsMissing);
    private sealed record KeepHistoryTransitionRow(string ChangeKind, string GraphKeySnapshot, long ChangedAtUtcTicks);
    private sealed record KeepStateRow(
        long LibraryId,
        long DuplicateGroupId,
        long? KeepTrackId,
        string Status,
        long UpdatedAtUtcTicks);
}