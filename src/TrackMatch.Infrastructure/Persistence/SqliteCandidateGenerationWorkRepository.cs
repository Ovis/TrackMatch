using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 指定LibraryのLibraryTrack Candidate Generation Pending状態をSQLiteで管理する。
/// </summary>
public sealed class SqliteCandidateGenerationWorkRepository(
    SqliteDatabase database,
    long libraryId) : ICandidateGenerationWorkRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlySet<long>> GetPendingTrackIdsAsync(CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<long>(new CommandDefinition(
            """
            SELECT TrackId
            FROM LibraryTracks
            WHERE LibraryId = @LibraryId
              AND (
                    CandidateGenerationPending = 1
                 OR CandidateGenerationVersion IS NULL
                 OR CandidateGenerationVersion <> @Version);
            """,
            new { LibraryId = libraryId, Version = CandidateGenerationVersion.Current },
            cancellationToken: cancellationToken));
        return ids.ToHashSet();
    }

    /// <inheritdoc />
    public async Task MarkCompletedAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        if (trackIds.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE LibraryTracks
            SET CandidateGenerationPending = 0,
                CandidateGenerationVersion = @Version
            WHERE LibraryId = @LibraryId
              AND TrackId IN @TrackIds;
            """,
            new
            {
                LibraryId = libraryId,
                Version = CandidateGenerationVersion.Current,
                TrackIds = trackIds.Distinct().ToArray(),
            },
            cancellationToken: cancellationToken));
    }
}
