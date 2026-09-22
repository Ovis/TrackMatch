using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Library単位のCandidate Generation完了状態をSQLiteへ保存する。
/// </summary>
public sealed class SqliteCandidateGenerationStateRepository(SqliteDatabase database, long libraryId)
    : ICandidateGenerationStateRepository
{
    /// <inheritdoc />
    public async Task<CandidateGenerationState?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<CandidateGenerationState>(new CommandDefinition(
            """
            SELECT CandidateGenerationAlgorithmVersion, FingerprintAlgorithm,
                   SegmentLengthItems, SegmentStrideItems,
                   MaximumSegmentHashHammingDistance, MinimumDominantOffsetHits
            FROM CandidateGenerationStates
            WHERE LibraryId = @LibraryId;
            """,
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task SaveAsync(CandidateGenerationState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO CandidateGenerationStates (
                LibraryId, CandidateGenerationAlgorithmVersion, FingerprintAlgorithm,
                SegmentLengthItems, SegmentStrideItems, MaximumSegmentHashHammingDistance,
                MinimumDominantOffsetHits, CompletedAtUtcTicks)
            VALUES (
                @LibraryId, @CandidateGenerationAlgorithmVersion, @FingerprintAlgorithm,
                @SegmentLengthItems, @SegmentStrideItems, @MaximumSegmentHashHammingDistance,
                @MinimumDominantOffsetHits, @CompletedAtUtcTicks)
            ON CONFLICT (LibraryId) DO UPDATE SET
                CandidateGenerationAlgorithmVersion = excluded.CandidateGenerationAlgorithmVersion,
                FingerprintAlgorithm = excluded.FingerprintAlgorithm,
                SegmentLengthItems = excluded.SegmentLengthItems,
                SegmentStrideItems = excluded.SegmentStrideItems,
                MaximumSegmentHashHammingDistance = excluded.MaximumSegmentHashHammingDistance,
                MinimumDominantOffsetHits = excluded.MinimumDominantOffsetHits,
                CompletedAtUtcTicks = excluded.CompletedAtUtcTicks;
            """,
            new
            {
                LibraryId = libraryId,
                state.CandidateGenerationAlgorithmVersion,
                state.FingerprintAlgorithm,
                state.SegmentLengthItems,
                state.SegmentStrideItems,
                state.MaximumSegmentHashHammingDistance,
                state.MinimumDominantOffsetHits,
                CompletedAtUtcTicks = DateTime.UtcNow.Ticks,
            },
            cancellationToken: cancellationToken));
    }
}
