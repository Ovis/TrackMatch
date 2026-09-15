using Dapper;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Library Membership変更後に、現在のLibraryから到達できなくなったDuplicate Group Keep Current Stateを履歴化して整理する。
/// </summary>
public sealed class SqliteLibraryKeepStateMaintenance(SqliteDatabase database)
{
    /// <summary>
    /// Global Duplicate GroupにActive Membershipを1件も持たないLibrary固有Keep Current Stateを履歴へ退避して削除する。
    /// </summary>
    /// <remarks>
    /// Root Remap等でMembershipだけが外れた場合に、後からMembershipが再追加された際の古いKeep自動復活を防ぐ。
    /// Current Stateの失効自体も設計上の状態遷移なので、削除前にHistoryへ記録する。
    /// </remarks>
    public async Task CleanupUnrelatedCurrentStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ArchiveAndDeleteUnrelatedCurrentStatesAsync(
            connection,
            transaction,
            libraryId: null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 指定Libraryまたは全Libraryから、現在GroupへActive Membershipを持たなくなったKeep Current Stateを履歴化して削除する。
    /// </summary>
    internal static async Task ArchiveAndDeleteUnrelatedCurrentStatesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        long? libraryId,
        CancellationToken cancellationToken)
    {
        const string scopePredicate = """
            (@LibraryId IS NULL OR ks.LibraryId = @LibraryId)
            AND NOT EXISTS (
                SELECT 1
                FROM DuplicateGroupTracks gt
                INNER JOIN LibraryTracks lt
                    ON lt.TrackId = gt.TrackId
                   AND lt.LibraryId = ks.LibraryId
                INNER JOIN Tracks t
                    ON t.Id = lt.TrackId
                   AND t.IsMissing = 0
                WHERE gt.DuplicateGroupId = ks.DuplicateGroupId)
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO LibraryDuplicateGroupKeepHistory (
                LibraryId, LibraryNameSnapshot, DuplicateGroupId, GraphKeySnapshot,
                KeepTrackId, Status, ChangeKind, ChangedAtUtcTicks, Note)
            SELECT ks.LibraryId, l.Name, ks.DuplicateGroupId, g.GraphKey,
                   ks.KeepTrackId, ks.Status, 'ScopeRemoved', @ChangedAtUtcTicks, NULL
            FROM LibraryDuplicateGroupKeepStates ks
            INNER JOIN Libraries l ON l.Id = ks.LibraryId
            INNER JOIN DuplicateGroups g ON g.Id = ks.DuplicateGroupId
            WHERE {scopePredicate};

            DELETE FROM LibraryDuplicateGroupKeepStates AS ks
            WHERE {scopePredicate};
            """,
            new { LibraryId = libraryId, ChangedAtUtcTicks = DateTime.UtcNow.Ticks },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Library削除前に、そのLibraryのKeep Current Stateをすべて履歴へ退避する。
    /// </summary>
    internal static async Task ArchiveLibraryCurrentStatesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        long libraryId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO LibraryDuplicateGroupKeepHistory (
                LibraryId, LibraryNameSnapshot, DuplicateGroupId, GraphKeySnapshot,
                KeepTrackId, Status, ChangeKind, ChangedAtUtcTicks, Note)
            SELECT ks.LibraryId, l.Name, ks.DuplicateGroupId, g.GraphKey,
                   ks.KeepTrackId, ks.Status, 'LibraryDeleted', @ChangedAtUtcTicks, NULL
            FROM LibraryDuplicateGroupKeepStates ks
            INNER JOIN Libraries l ON l.Id = ks.LibraryId
            INNER JOIN DuplicateGroups g ON g.Id = ks.DuplicateGroupId
            WHERE ks.LibraryId = @LibraryId;
            """,
            new { LibraryId = libraryId, ChangedAtUtcTicks = DateTime.UtcNow.Ticks },
            transaction,
            cancellationToken: cancellationToken));
    }
}
