using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Candidate Generation用Segment SketchをSQLiteへ保存し、Fingerprint未変更時の再計算を避ける。
/// </summary>
public sealed class SqliteFingerprintSegmentSketchRepository(SqliteDatabase database) : IFingerprintSegmentSketchRepository
{
    public async Task<IReadOnlyDictionary<long, DateTime>> GetTrackStatesAsync(
        int algorithm,
        CandidateGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TrackId, MAX(FingerprintExtractedAtUtcTicks) AS FingerprintExtractedAtUtcTicks
            FROM CandidateSegmentSketches
            WHERE Algorithm = @Algorithm
              AND SegmentLengthItems = @SegmentLengthItems
              AND SegmentStrideItems = @SegmentStrideItems
              AND MaximumSegmentHashDistance = @MaximumSegmentHashDistance
            GROUP BY TrackId;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SketchStateRow>(new CommandDefinition(
            sql,
            CreateConfigParameters(algorithm, options),
            cancellationToken: cancellationToken));
        return rows.ToDictionary(
            row => row.TrackId,
            row => new DateTime(row.FingerprintExtractedAtUtcTicks, DateTimeKind.Utc));
    }

    public async Task<IReadOnlyList<FingerprintSegmentSketch>> GetAllAsync(
        int algorithm,
        CandidateGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TrackId, SegmentIndex, Hash
            FROM CandidateSegmentSketches
            WHERE Algorithm = @Algorithm
              AND SegmentLengthItems = @SegmentLengthItems
              AND SegmentStrideItems = @SegmentStrideItems
              AND MaximumSegmentHashDistance = @MaximumSegmentHashDistance
            ORDER BY TrackId, SegmentIndex;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SketchRow>(new CommandDefinition(
            sql,
            CreateConfigParameters(algorithm, options),
            cancellationToken: cancellationToken));
        return rows
            .Select(row => new FingerprintSegmentSketch(
                row.TrackId,
                checked((int)row.SegmentIndex),
                unchecked((uint)row.Hash)))
            .ToArray();
    }

    public async Task ReplaceTrackAsync(
        StoredFingerprint fingerprint,
        CandidateGenerationOptions options,
        IReadOnlyCollection<FingerprintSegmentSketch> sketches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sketches);

        const string deleteSql = "DELETE FROM CandidateSegmentSketches WHERE TrackId = @TrackId AND Algorithm = @Algorithm;";
        const string insertSql = """
            INSERT INTO CandidateSegmentSketches (
                TrackId, Algorithm, SegmentLengthItems, SegmentStrideItems,
                MaximumSegmentHashDistance, SegmentIndex, Hash, FingerprintExtractedAtUtcTicks)
            VALUES (
                @TrackId, @Algorithm, @SegmentLengthItems, @SegmentStrideItems,
                @MaximumSegmentHashDistance, @SegmentIndex, @Hash, @FingerprintExtractedAtUtcTicks);
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            deleteSql,
            new { fingerprint.TrackId, fingerprint.Algorithm },
            transaction,
            cancellationToken: cancellationToken));

        if (sketches.Count != 0)
        {
            var parameters = sketches.Select(sketch => new
            {
                fingerprint.TrackId,
                fingerprint.Algorithm,
                options.SegmentLengthItems,
                options.SegmentStrideItems,
                MaximumSegmentHashDistance = options.MaximumSegmentHashHammingDistance,
                sketch.SegmentIndex,
                Hash = unchecked((long)sketch.Hash),
                FingerprintExtractedAtUtcTicks = fingerprint.ExtractedAtUtc.ToUniversalTime().Ticks,
            });
            await connection.ExecuteAsync(new CommandDefinition(
                insertSql,
                parameters,
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task PruneAsync(
        int algorithm,
        CandidateGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM CandidateSegmentSketches
            WHERE Algorithm = @Algorithm
              AND (
                    SegmentLengthItems <> @SegmentLengthItems
                 OR SegmentStrideItems <> @SegmentStrideItems
                 OR MaximumSegmentHashDistance <> @MaximumSegmentHashDistance
                 OR NOT EXISTS (
                        SELECT 1
                        FROM Fingerprints f
                        INNER JOIN Tracks t ON t.Id = f.TrackId
                        WHERE f.TrackId = CandidateSegmentSketches.TrackId
                          AND f.Algorithm = @Algorithm
                          AND t.IsMissing = 0));
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            CreateConfigParameters(algorithm, options),
            cancellationToken: cancellationToken));
    }

    private static object CreateConfigParameters(int algorithm, CandidateGenerationOptions options)
        => new
        {
            Algorithm = algorithm,
            options.SegmentLengthItems,
            options.SegmentStrideItems,
            MaximumSegmentHashDistance = options.MaximumSegmentHashHammingDistance,
        };

    private sealed record SketchStateRow(long TrackId, long FingerprintExtractedAtUtcTicks);
    private sealed record SketchRow(long TrackId, long SegmentIndex, long Hash);
}
