using Dapper;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Track IDからSQLite上のTrackを参照する。
/// </summary>
public sealed class SqliteTrackLookupRepository(SqliteDatabase database) : ITrackLookupRepository
{
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
}
