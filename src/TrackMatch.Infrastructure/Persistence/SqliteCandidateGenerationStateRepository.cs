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
        var row = await connection.QuerySingleOrDefaultAsync<CandidateGenerationStateRow>(new CommandDefinition(
            """
            SELECT CandidateGenerationAlgorithmVersion, FingerprintAlgorithm,
                   SegmentLengthItems, SegmentStrideItems,
                   MaximumSegmentHashHammingDistance, MinimumDominantOffsetHits
            FROM CandidateGenerationStates
            WHERE LibraryId = @LibraryId;
            """,
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new CandidateGenerationState(
                checked((int)row.CandidateGenerationAlgorithmVersion),
                checked((int)row.FingerprintAlgorithm),
                checked((int)row.SegmentLengthItems),
                checked((int)row.SegmentStrideItems),
                checked((int)row.MaximumSegmentHashHammingDistance),
                checked((int)row.MinimumDominantOffsetHits));
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

    private sealed record CandidateGenerationStateRow(
        long CandidateGenerationAlgorithmVersion,
        long FingerprintAlgorithm,
        long SegmentLengthItems,
        long SegmentStrideItems,
        long MaximumSegmentHashHammingDistance,
        long MinimumDominantOffsetHits);
}
