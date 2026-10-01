using Dapper;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Global GroupとLibrary Keep ProjectionのDirty状態をSQLiteへ保存する。
/// </summary>
public sealed class SqliteProjectionStateRepository(SqliteDatabase database) : IProjectionStateRepository
{
    /// <inheritdoc />
    public async Task<bool> IsGlobalGroupsDirtyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT GlobalGroupsDirty FROM ProjectionStates WHERE Id = 1;",
            cancellationToken: cancellationToken)) != 0;
    }

    /// <inheritdoc />
    public async Task<bool> IsLibraryKeepProjectionDirtyAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        ValidateLibraryId(libraryId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT KeepProjectionDirty FROM LibraryProjectionStates WHERE LibraryId = @LibraryId;",
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken)) != 0;
    }

    /// <inheritdoc />
    public async Task MarkReviewMutationStartedAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        ValidateLibraryId(libraryId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE ProjectionStates SET GlobalGroupsDirty = 1 WHERE Id = 1;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO LibraryProjectionStates (LibraryId, KeepProjectionDirty)
            VALUES (@LibraryId, 1)
            ON CONFLICT(LibraryId) DO UPDATE SET KeepProjectionDirty = 1;
            """,
            new { LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken));
        if (changed != 1)
        {
            throw new InvalidOperationException("現在LibraryのProjection Dirty状態を保存できませんでした。");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetLibraryKeepProjectionDirtyAsync(
        long libraryId,
        bool isDirty,
        CancellationToken cancellationToken = default)
    {
        ValidateLibraryId(libraryId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO LibraryProjectionStates (LibraryId, KeepProjectionDirty)
            VALUES (@LibraryId, @IsDirty)
            ON CONFLICT(LibraryId) DO UPDATE SET KeepProjectionDirty = excluded.KeepProjectionDirty;
            """,
            new { LibraryId = libraryId, IsDirty = isDirty ? 1 : 0 },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task SetGlobalGroupsDirtyAsync(
        bool isDirty,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE ProjectionStates SET GlobalGroupsDirty = @IsDirty WHERE Id = 1;",
            new { IsDirty = isDirty ? 1 : 0 },
            cancellationToken: cancellationToken));
    }

    private static void ValidateLibraryId(long libraryId)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }
    }
}
