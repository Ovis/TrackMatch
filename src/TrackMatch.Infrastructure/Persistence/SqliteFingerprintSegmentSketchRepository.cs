using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Candidate Generation用Segment SketchをSQLiteへ保存し、Fingerprint未変更時の再計算を避ける。
/// </summary>
public sealed class SqliteFingerprintSegmentSketchRepository(
    SqliteDatabase database,
    long? libraryId = null) : IFingerprintSegmentSketchRepository
{
    public async Task<IReadOnlyDictionary<long, DateTime>> GetTrackStatesAsync(
        int algorithm,
        CandidateGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT s.TrackId, s.FingerprintExtractedAtUtcTicks
            FROM CandidateSegmentSketches s
            INNER JOIN Tracks t ON t.Id = s.TrackId
            WHERE s.Algorithm = @Algorithm
              AND s.SegmentLengthItems = @SegmentLengthItems
              AND s.SegmentStrideItems = @SegmentStrideItems
              AND s.MaximumSegmentHashDistance = @MaximumSegmentHashDistance
              AND (@LibraryId IS NULL OR t.LibraryId = @LibraryId);
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SketchStateRow>(new CommandDefinition(
            sql,
            CreateConfigParameters(algorithm, options),
            cancellationToken: cancellationToken));

        // Microsoft.Data.SqliteではMAX等の集約式が元カラムのINTEGER型情報を保持せず、
        // DapperがByte[]としてmaterializeしようとする場合があるため、最新時刻はC#側で選ぶ。
        return rows
            .GroupBy(row => row.TrackId)
            .ToDictionary(
                group => group.Key,
                group => new DateTime(group.Max(row => row.FingerprintExtractedAtUtcTicks), DateTimeKind.Utc));
    }

    public async Task<IReadOnlyList<FingerprintSegmentSketch>> GetAllAsync(
        int algorithm,
        CandidateGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT s.TrackId, s.SegmentIndex, s.Hash
            FROM CandidateSegmentSketches s
            INNER JOIN Tracks t ON t.Id = s.TrackId
            WHERE s.Algorithm = @Algorithm
              AND s.SegmentLengthItems = @SegmentLengthItems
              AND s.SegmentStrideItems = @SegmentStrideItems
              AND s.MaximumSegmentHashDistance = @MaximumSegmentHashDistance
              AND (@LibraryId IS NULL OR t.LibraryId = @LibraryId)
            ORDER BY s.TrackId, s.SegmentIndex;
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
        await EnsureTrackInScopeAsync(connection, fingerprint.TrackId, cancellationToken);
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
            WHERE TrackId IN (
                SELECT t.Id
                FROM Tracks t
                WHERE @LibraryId IS NULL OR t.LibraryId = @LibraryId)
              AND Algorithm = @Algorithm
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

    private async Task EnsureTrackInScopeAsync(
        System.Data.Common.DbConnection connection,
        long trackId,
        CancellationToken cancellationToken)
    {
        if (libraryId is null)
        {
            return;
        }

        var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Tracks WHERE Id = @TrackId AND LibraryId = @LibraryId;",
            new { TrackId = trackId, LibraryId = libraryId },
            cancellationToken: cancellationToken));
        if (exists != 1)
        {
            throw new InvalidOperationException("Library scope外のTrackへSegment Sketchを保存できません。");
        }
    }

    private object CreateConfigParameters(int algorithm, CandidateGenerationOptions options)
        => new
        {
            Algorithm = algorithm,
            LibraryId = libraryId,
            options.SegmentLengthItems,
            options.SegmentStrideItems,
            MaximumSegmentHashDistance = options.MaximumSegmentHashHammingDistance,
        };

    private sealed record SketchStateRow(long TrackId, long FingerprintExtractedAtUtcTicks);
    private sealed record SketchRow(long TrackId, long SegmentIndex, long Hash);
}
