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

        // Keep Current StateをTopology変更前にHistoryへ退避する。Merge/Split後に同じ値を継承する場合でも、
        // どの旧Groupから引き継がれたか追跡できるようGraphKey Snapshotを残す。
        foreach (var state in oldKeepStates)
        {
            var oldGroup = oldGroups.SingleOrDefault(group => group.Id == state.DuplicateGroupId);
            if (oldGroup is null)
            {
                continue;
            }

            await InsertKeepHistoryAsync(
                connection,
                transaction,
                state.LibraryId,
                state.DuplicateGroupId,
                BuildGraphKey(oldGroup.TrackIds),
                state.KeepTrackId,
                state.Status,
                "GroupRebuild",
                note: null,
                cancellationToken);
        }

        // TrackIdのGlobal UNIQUE制約との一時衝突を避けるため、構成行を先に全削除してからGroup本体を更新する。
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM DuplicateGroupTracks; DELETE FROM LibraryDuplicateGroupKeepStates;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        var requestedExistingIds = groups
            .Where(group => group.ExistingGroupId is not null)
            .Select(group => group.ExistingGroupId!.Value)
            .ToHashSet();
        var existingIds = oldGroups.Select(group => group.Id).ToHashSet();
        if (requestedExistingIds.Any(id => !existingIds.Contains(id)))
        {
            throw new InvalidOperationException("再利用対象のGlobal Duplicate Group IDが現行構成に存在しません。");
        }

        foreach (var obsoleteId in existingIds.Where(id => !requestedExistingIds.Contains(id)))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM DuplicateGroups WHERE Id = @Id;",
                new { Id = obsoleteId },
                transaction,
                cancellationToken: cancellationToken));
        }

        var persisted = new List<(long Id, DuplicateGroupRebuildItem Plan)>(groups.Count);
        foreach (var plan in groups)
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
            persisted.Add((groupId, plan));
        }

        await RestoreKeepStatesAsync(connection, transaction, oldGroups, oldKeepStates, persisted, cancellationToken);
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

        var current = await connection.QuerySingleOrDefaultAsync<KeepStateRow>(new CommandDefinition(
            """
            SELECT LibraryId, DuplicateGroupId, KeepTrackId, Status, UpdatedAtUtcTicks
            FROM LibraryDuplicateGroupKeepStates
            WHERE LibraryId = @LibraryId AND DuplicateGroupId = @GroupId;
            """,
            new { LibraryId = libraryId, GroupId = groupId },
            transaction,
            cancellationToken: cancellationToken));
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
        var oldGroupById = oldGroups.ToDictionary(group => group.Id);
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

                var selectedKeeps = relevantStates
                    .Where(state => string.Equals(state.Status, nameof(DuplicateGroupKeepStatus.Selected), StringComparison.Ordinal)
                        && state.KeepTrackId is not null
                        && newTrackSet.Contains(state.KeepTrackId.Value))
                    .Select(state => state.KeepTrackId!.Value)
                    .Distinct()
                    .ToArray();

                long? keepTrackId = null;
                var status = DuplicateGroupKeepStatus.Unselected;
                if (selectedKeeps.Length == 1)
                {
                    var isMissing = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                        "SELECT IsMissing FROM Tracks WHERE Id = @TrackId;",
                        new { TrackId = selectedKeeps[0] },
                        transaction,
                        cancellationToken: cancellationToken)) != 0;
                    if (isMissing)
                    {
                        status = DuplicateGroupKeepStatus.Missing;
                    }
                    else
                    {
                        keepTrackId = selectedKeeps[0];
                        status = DuplicateGroupKeepStatus.Selected;
                    }
                }
                else if (selectedKeeps.Length > 1
                         || relevantStates.Any(state => string.Equals(state.Status, nameof(DuplicateGroupKeepStatus.Conflict), StringComparison.Ordinal)))
                {
                    status = DuplicateGroupKeepStatus.Conflict;
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
    private sealed record KeepStateRow(
        long LibraryId,
        long DuplicateGroupId,
        long? KeepTrackId,
        string Status,
        long UpdatedAtUtcTicks);
}
