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
    public async Task<bool> IsHumanVerdictUsableAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var status = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT ContentVerificationStatus FROM Tracks WHERE Id = @TrackId AND IsMissing = 0;",
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
        return string.Equals(status, "Verified", StringComparison.Ordinal);
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackLibraryReference>> GetLibrariesAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<LibraryReferenceRow>(new CommandDefinition(
            """
            SELECT l.Id, l.Name
            FROM LibraryTracks lt
            INNER JOIN Libraries l ON l.Id = lt.LibraryId
            WHERE lt.TrackId = @TrackId
            ORDER BY l.Name COLLATE NOCASE, l.Id;
            """,
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
        return rows.Select(row => new TrackLibraryReference(row.Id, row.Name)).ToArray();
    }

    private sealed record LibraryReferenceRow(long Id, string Name);
}
