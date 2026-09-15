using Dapper;
using TrackMatch.Core.Libraries;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Library管理GUIの確認表示に必要なMembership件数をSQLiteから取得する。
/// </summary>
public sealed class SqliteLibraryStatisticsRepository(SqliteDatabase database)
{
    /// <summary>
    /// 指定Root由来のLibraryTrack数を取得する。
    /// </summary>
    public async Task<long> GetRootTrackCountAsync(long rootId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM LibraryTracks WHERE RootId = @RootId;",
            new { RootId = rootId },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Library削除確認に表示するRoot数とMembership Track数を取得する。
    /// </summary>
    public async Task<LibraryDeleteSummary> GetLibraryDeleteSummaryAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<SummaryRow>(new CommandDefinition(
            """
            SELECT
                (SELECT COUNT(*) FROM LibraryRoots WHERE LibraryId = @LibraryId) AS RootCount,
                (SELECT COUNT(*) FROM LibraryTracks WHERE LibraryId = @LibraryId) AS TrackCount;
            """,
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken));
        return new LibraryDeleteSummary(row.RootCount, row.TrackCount);
    }

    private sealed record SummaryRow(long RootCount, long TrackCount);
}
