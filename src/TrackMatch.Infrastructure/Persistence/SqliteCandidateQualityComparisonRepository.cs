using Dapper;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Quality;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Candidate固有の音質比較キャッシュをSQLiteへ保存する。
/// </summary>
public sealed class SqliteCandidateQualityComparisonRepository(SqliteDatabase database) : ICandidateQualityComparisonRepository
{
    /// <inheritdoc />
    public async Task<CandidateQualityComparison?> GetAsync(
        long trackIdA,
        long trackIdB,
        CancellationToken cancellationToken = default)
    {
        var (a, b) = NormalizePair(trackIdA, trackIdB);
        const string sql = """
            SELECT TrackIdA, TrackIdB, ComparisonVersion, Status, MatchedLoudnessDifferenceLu,
                   GainDifferenceMeanDb, GainDifferenceStandardDeviationDb,
                   PeakToLoudnessRatioDifferenceDb, LoudnessRangeDifferenceLu,
                   IsPrimarilyGainDifference, RelativeHighFrequencyDifference,
                   ComparedAtUtcTicks, FailureReason
            FROM CandidateQualityComparisons
            WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            sql,
            new { TrackIdA = a, TrackIdB = b },
            cancellationToken: cancellationToken));
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        CandidateQualityComparison comparison,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var (a, b) = NormalizePair(comparison.TrackIdA, comparison.TrackIdB);
        if (a != comparison.TrackIdA || b != comparison.TrackIdB)
        {
            throw new ArgumentException("Candidate品質比較はTrackIdA < TrackIdBの順序で保存する必要があります。", nameof(comparison));
        }

        const string sql = """
            INSERT INTO CandidateQualityComparisons (
                TrackIdA, TrackIdB, ComparisonVersion, Status, MatchedLoudnessDifferenceLu,
                GainDifferenceMeanDb, GainDifferenceStandardDeviationDb,
                PeakToLoudnessRatioDifferenceDb, LoudnessRangeDifferenceLu,
                IsPrimarilyGainDifference, RelativeHighFrequencyDifference,
                ComparedAtUtcTicks, FailureReason)
            VALUES (
                @TrackIdA, @TrackIdB, @ComparisonVersion, @Status, @MatchedLoudnessDifferenceLu,
                @GainDifferenceMeanDb, @GainDifferenceStandardDeviationDb,
                @PeakToLoudnessRatioDifferenceDb, @LoudnessRangeDifferenceLu,
                @IsPrimarilyGainDifference, @RelativeHighFrequencyDifference,
                @ComparedAtUtcTicks, @FailureReason)
            ON CONFLICT(TrackIdA, TrackIdB) DO UPDATE SET
                ComparisonVersion = excluded.ComparisonVersion,
                Status = excluded.Status,
                MatchedLoudnessDifferenceLu = excluded.MatchedLoudnessDifferenceLu,
                GainDifferenceMeanDb = excluded.GainDifferenceMeanDb,
                GainDifferenceStandardDeviationDb = excluded.GainDifferenceStandardDeviationDb,
                PeakToLoudnessRatioDifferenceDb = excluded.PeakToLoudnessRatioDifferenceDb,
                LoudnessRangeDifferenceLu = excluded.LoudnessRangeDifferenceLu,
                IsPrimarilyGainDifference = excluded.IsPrimarilyGainDifference,
                RelativeHighFrequencyDifference = excluded.RelativeHighFrequencyDifference,
                ComparedAtUtcTicks = excluded.ComparedAtUtcTicks,
                FailureReason = excluded.FailureReason;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                comparison.TrackIdA,
                comparison.TrackIdB,
                comparison.ComparisonVersion,
                Status = comparison.Status.ToString(),
                comparison.MatchedLoudnessDifferenceLu,
                comparison.GainDifferenceMeanDb,
                comparison.GainDifferenceStandardDeviationDb,
                comparison.PeakToLoudnessRatioDifferenceDb,
                comparison.LoudnessRangeDifferenceLu,
                IsPrimarilyGainDifference = comparison.IsPrimarilyGainDifference is null ? (long?)null : comparison.IsPrimarilyGainDifference.Value ? 1L : 0L,
                comparison.RelativeHighFrequencyDifference,
                ComparedAtUtcTicks = comparison.ComparedAtUtc?.Ticks,
                comparison.FailureReason,
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long trackIdA, long trackIdB, CancellationToken cancellationToken = default)
    {
        var (a, b) = NormalizePair(trackIdA, trackIdB);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateQualityComparisons WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;",
            new { TrackIdA = a, TrackIdB = b },
            cancellationToken: cancellationToken));
    }

    private static (long A, long B) NormalizePair(long trackIdA, long trackIdB)
    {
        if (trackIdA <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIdA));
        }

        if (trackIdB <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIdB));
        }

        if (trackIdA == trackIdB)
        {
            throw new ArgumentException("同一Track同士はCandidate品質比較として扱えません。", nameof(trackIdB));
        }

        return trackIdA < trackIdB ? (trackIdA, trackIdB) : (trackIdB, trackIdA);
    }

    private static CandidateQualityComparison ToDomain(Row row)
        => new(
            row.TrackIdA,
            row.TrackIdB,
            checked((int)row.ComparisonVersion),
            Enum.Parse<QualityAnalysisStatus>(row.Status, ignoreCase: false),
            row.MatchedLoudnessDifferenceLu,
            row.GainDifferenceMeanDb,
            row.GainDifferenceStandardDeviationDb,
            row.PeakToLoudnessRatioDifferenceDb,
            row.LoudnessRangeDifferenceLu,
            row.IsPrimarilyGainDifference is null ? null : row.IsPrimarilyGainDifference != 0,
            row.RelativeHighFrequencyDifference,
            row.ComparedAtUtcTicks is null ? null : new DateTime(row.ComparedAtUtcTicks.Value, DateTimeKind.Utc),
            row.FailureReason);

    private sealed record Row(
        long TrackIdA,
        long TrackIdB,
        long ComparisonVersion,
        string Status,
        double? MatchedLoudnessDifferenceLu,
        double? GainDifferenceMeanDb,
        double? GainDifferenceStandardDeviationDb,
        double? PeakToLoudnessRatioDifferenceDb,
        double? LoudnessRangeDifferenceLu,
        long? IsPrimarilyGainDifference,
        double? RelativeHighFrequencyDifference,
        long? ComparedAtUtcTicks,
        string? FailureReason);
}
