using Dapper;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Global TrackとLibrary MembershipをSQLiteから参照する。
/// </summary>
public sealed class SqliteTrackLookupRepository(SqliteDatabase database) : ITrackLookupRepository
{
    /// <inheritdoc />
    public async Task<StoredTrack?> GetByIdAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var path = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT Path FROM Tracks WHERE Id = @TrackId;",
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
        if (path is null)
        {
            return null;
        }

        return await new SqliteTrackRepository(database).GetByPathAsync(path, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsInLibraryAsync(
        long trackId,
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM LibraryTracks WHERE LibraryId = @LibraryId AND TrackId = @TrackId;",
            new { LibraryId = libraryId, TrackId = trackId },
            cancellationToken: cancellationToken));
        return count != 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetLibraryIdsAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT LibraryId FROM LibraryTracks WHERE TrackId = @TrackId ORDER BY LibraryId;",
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
        return ids.ToArray();
    }
}
