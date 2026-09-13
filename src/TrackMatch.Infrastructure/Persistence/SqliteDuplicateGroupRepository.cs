using Dapper;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 確定済み重複グループと所属TrackをSQLiteへ永続化する。
/// </summary>
public sealed class SqliteDuplicateGroupRepository(SqliteDatabase database) : IDuplicateGroupRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DuplicateGroup>> GetByLibraryIdAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        const string groupSql = """
            SELECT Id, LibraryId, KeepTrackId
            FROM DuplicateGroups
            WHERE LibraryId = @LibraryId
            ORDER BY Id;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<GroupRow>(new CommandDefinition(
            groupSql,
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken))).ToArray();

        if (rows.Length == 0)
        {
            return [];
        }

        const string trackSql = """
            SELECT gt.DuplicateGroupId, gt.TrackId
            FROM DuplicateGroupTracks gt
            INNER JOIN DuplicateGroups g ON g.Id = gt.DuplicateGroupId
            WHERE g.LibraryId = @LibraryId
            ORDER BY gt.DuplicateGroupId, gt.TrackId;
            """;
        var trackRows = await connection.QueryAsync<GroupTrackRow>(new CommandDefinition(
            trackSql,
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken));
        var tracksByGroup = trackRows
            .GroupBy(row => row.DuplicateGroupId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<long>)group.Select(row => row.TrackId).ToArray());

        return rows.Select(row => CreateGroup(row, tracksByGroup.GetValueOrDefault(row.Id) ?? [])).ToArray();
    }

    /// <inheritdoc />
    public async Task<DuplicateGroup?> GetByTrackIdAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        const string sql = """
            SELECT g.Id, g.LibraryId, g.KeepTrackId
            FROM DuplicateGroups g
            INNER JOIN DuplicateGroupTracks gt ON gt.DuplicateGroupId = g.Id
            WHERE gt.TrackId = @TrackId;
            """;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<GroupRow>(new CommandDefinition(
            sql,
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
        return row is null ? null : await LoadGroupAsync(connection, row, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DuplicateGroup?> GetByIdAsync(
        long groupId,
        CancellationToken cancellationToken = default)
    {
        if (groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupId));
        }

        const string sql = "SELECT Id, LibraryId, KeepTrackId FROM DuplicateGroups WHERE Id = @Id;";
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<GroupRow>(new CommandDefinition(
            sql,
            new { Id = groupId },
            cancellationToken: cancellationToken));
        return row is null ? null : await LoadGroupAsync(connection, row, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReplaceLibraryAsync(
        long libraryId,
        IReadOnlyCollection<DuplicateGroupRebuildItem> groups,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        ArgumentNullException.ThrowIfNull(groups);
        ValidateRebuild(groups);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existingIds = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT Id FROM DuplicateGroups WHERE LibraryId = @LibraryId ORDER BY Id;",
            new { LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken))).ToHashSet();
        var requestedExistingIds = groups
            .Where(group => group.ExistingGroupId is not null)
            .Select(group => group.ExistingGroupId!.Value)
            .ToArray();

        if (requestedExistingIds.Distinct().Count() != requestedExistingIds.Length
            || requestedExistingIds.Any(id => !existingIds.Contains(id)))
        {
            throw new InvalidOperationException("再利用対象の重複グループIDが現在のLibrary構成と一致しません。");
        }

        // 結合・分割ではTrackが別グループへ移るため、UNIQUE(TrackId)との一時衝突を避ける目的で
        // 同一Libraryの所属行を先にすべて外し、Group本体の更新後に新構成をまとめて入れ直す。
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM DuplicateGroupTracks
            WHERE DuplicateGroupId IN (SELECT Id FROM DuplicateGroups WHERE LibraryId = @LibraryId);
            """,
            new { LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken));

        var retainedIds = requestedExistingIds.ToHashSet();
        foreach (var obsoleteId in existingIds.Where(id => !retainedIds.Contains(id)))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM DuplicateGroups WHERE Id = @Id;",
                new { Id = obsoleteId },
                transaction,
                cancellationToken: cancellationToken));
        }

        var persistedGroups = new List<(long Id, DuplicateGroupRebuildItem Group)>(groups.Count);
        foreach (var group in groups)
        {
            long groupId;
            if (group.ExistingGroupId is { } existingId)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE DuplicateGroups SET KeepTrackId = @KeepTrackId WHERE Id = @Id AND LibraryId = @LibraryId;",
                    new { Id = existingId, LibraryId = libraryId, group.KeepTrackId },
                    transaction,
                    cancellationToken: cancellationToken));
                groupId = existingId;
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO DuplicateGroups (LibraryId, KeepTrackId) VALUES (@LibraryId, @KeepTrackId);",
                    new { LibraryId = libraryId, group.KeepTrackId },
                    transaction,
                    cancellationToken: cancellationToken));
                groupId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT last_insert_rowid();",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            }

            persistedGroups.Add((groupId, group));
        }

        const string insertTrackSql = "INSERT INTO DuplicateGroupTracks (DuplicateGroupId, TrackId) VALUES (@DuplicateGroupId, @TrackId);";
        foreach (var persisted in persistedGroups)
        {
            foreach (var trackId in persisted.Group.TrackIds)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    insertTrackSql,
                    new { DuplicateGroupId = persisted.Id, TrackId = trackId },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static void ValidateRebuild(IReadOnlyCollection<DuplicateGroupRebuildItem> groups)
    {
        var allTrackIds = new HashSet<long>();
        foreach (var group in groups)
        {
            if (group.TrackIds.Count < 2
                || group.TrackIds.Any(trackId => trackId <= 0)
                || group.TrackIds.Distinct().Count() != group.TrackIds.Count)
            {
                throw new ArgumentException("重複グループには重複のない2件以上のTrackが必要です。", nameof(groups));
            }

            if (!group.TrackIds.Contains(group.KeepTrackId))
            {
                throw new ArgumentException("KeepTrackIdはグループ構成Trackに含まれている必要があります。", nameof(groups));
            }

            if (group.TrackIds.Any(trackId => !allTrackIds.Add(trackId)))
            {
                throw new ArgumentException("1つのTrackを複数の重複グループへ所属させることはできません。", nameof(groups));
            }
        }
    }

    private static DuplicateGroup CreateGroup(GroupRow row, IReadOnlyList<long> trackIds)
    {
        var group = new DuplicateGroup(row.Id, row.LibraryId, row.KeepTrackId, trackIds);
        group.Validate();
        return group;
    }

    private static async Task<DuplicateGroup> LoadGroupAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        GroupRow row,
        CancellationToken cancellationToken)
    {
        var trackIds = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT TrackId FROM DuplicateGroupTracks WHERE DuplicateGroupId = @Id ORDER BY TrackId;",
            new { row.Id },
            cancellationToken: cancellationToken))).ToArray();
        return CreateGroup(row, trackIds);
    }

    private sealed record GroupRow(long Id, long LibraryId, long KeepTrackId);
    private sealed record GroupTrackRow(long DuplicateGroupId, long TrackId);
}
