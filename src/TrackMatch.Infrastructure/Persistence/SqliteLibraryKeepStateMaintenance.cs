using Dapper;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Library Membership変更後に、現在のLibraryから到達できなくなったDuplicate Group Keep Current Stateを整理する。
/// </summary>
public sealed class SqliteLibraryKeepStateMaintenance(SqliteDatabase database)
{
    /// <summary>
    /// Global Duplicate GroupにActive Membershipを1件も持たないLibrary固有Keep Current Stateを削除する。
    /// </summary>
    /// <remarks>
    /// Keep履歴は削除しない。Root RemapやRoot削除でMembershipだけが外れた場合に、
    /// 後からMembershipが再追加された際の古いKeep自動復活を防ぐためのCurrent State整合処理である。
    /// </remarks>
    public async Task CleanupUnrelatedCurrentStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM LibraryDuplicateGroupKeepStates
            WHERE NOT EXISTS (
                SELECT 1
                FROM DuplicateGroupTracks gt
                INNER JOIN LibraryTracks lt
                    ON lt.TrackId = gt.TrackId
                   AND lt.LibraryId = LibraryDuplicateGroupKeepStates.LibraryId
                INNER JOIN Tracks t
                    ON t.Id = lt.TrackId
                   AND t.IsMissing = 0
                WHERE gt.DuplicateGroupId = LibraryDuplicateGroupKeepStates.DuplicateGroupId);
            """,
            cancellationToken: cancellationToken));
    }
}
