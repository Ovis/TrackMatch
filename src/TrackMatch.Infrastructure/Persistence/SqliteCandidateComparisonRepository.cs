using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 候補TrackペアのGlobal Fingerprint比較結果をSQLiteへ保存する。
/// </summary>
public sealed class SqliteCandidateComparisonRepository(
    SqliteDatabase database,
    long? libraryId = null) : ICandidateComparisonRepository
{
    public async Task ReplaceAllAsync(
        IReadOnlyCollection<CandidateComparison> comparisons,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparisons);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (libraryId is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM CandidateComparisons;",
                transaction: transaction,
                cancellationToken: cancellationToken));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM CandidateComparisons
                WHERE EXISTS (
                        SELECT 1 FROM LibraryTracks a
                        WHERE a.LibraryId = @LibraryId AND a.TrackId = CandidateComparisons.TrackIdA)
                  AND EXISTS (
                        SELECT 1 FROM LibraryTracks b
                        WHERE b.LibraryId = @LibraryId AND b.TrackId = CandidateComparisons.TrackIdB);
                """,
                new { LibraryId = libraryId },
                transaction,
                cancellationToken: cancellationToken));
        }

        await EnsureComparisonsInScopeAsync(connection, transaction, comparisons, cancellationToken);
        await UpsertCoreAsync(connection, transaction, comparisons, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        IReadOnlyCollection<CandidateComparison> comparisons,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        if (comparisons.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureComparisonsInScopeAsync(connection, transaction, comparisons, cancellationToken);

        // Machine Comparisonが変わった場合は分類だけ再計算する。Human VerdictはGlobalな人間判断なので維持する。
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateClassifications WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;",
            comparisons.Select(item => new { item.TrackIdA, item.TrackIdB }),
            transaction,
            cancellationToken: cancellationToken));

        await UpsertCoreAsync(connection, transaction, comparisons, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CandidateComparison>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT c.TrackIdA, c.TrackIdB, c.Similarity, c.BestOffsetItems, c.BestOffsetTicks,
                   c.MatchedItems, c.MatchedDurationTicks, c.CoverageA, c.CoverageB, c.DurationRatio
            FROM CandidateComparisons c
            INNER JOIN Tracks ta ON ta.Id = c.TrackIdA AND ta.IsMissing = 0
            INNER JOIN Tracks tb ON tb.Id = c.TrackIdB AND tb.IsMissing = 0
            WHERE c.ComparisonVersion = @ComparisonVersion
              AND (
                    @LibraryId IS NULL
                 OR (
                        EXISTS (SELECT 1 FROM LibraryTracks a WHERE a.LibraryId = @LibraryId AND a.TrackId = c.TrackIdA)
                    AND EXISTS (SELECT 1 FROM LibraryTracks b WHERE b.LibraryId = @LibraryId AND b.TrackId = c.TrackIdB)))
            ORDER BY c.Similarity DESC, c.TrackIdA, c.TrackIdB;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ComparisonRow>(new CommandDefinition(
            sql,
            new
            {
                LibraryId = libraryId,
                ComparisonVersion = CandidateComparisonAlgorithmVersion.Current,
            },
            cancellationToken: cancellationToken));
        return rows.Select(ToDomain).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<CandidatePairKey, DateTime>> GetComparedAtUtcAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT c.TrackIdA, c.TrackIdB, c.ComparedAtUtcTicks
            FROM CandidateComparisons c
            INNER JOIN Tracks ta ON ta.Id = c.TrackIdA AND ta.IsMissing = 0
            INNER JOIN Tracks tb ON tb.Id = c.TrackIdB AND tb.IsMissing = 0
            WHERE c.ComparisonVersion = @ComparisonVersion
              AND (
                    @LibraryId IS NULL
                 OR (
                        EXISTS (SELECT 1 FROM LibraryTracks a WHERE a.LibraryId = @LibraryId AND a.TrackId = c.TrackIdA)
                    AND EXISTS (SELECT 1 FROM LibraryTracks b WHERE b.LibraryId = @LibraryId AND b.TrackId = c.TrackIdB)));
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ComparedAtRow>(new CommandDefinition(
            sql,
            new
            {
                LibraryId = libraryId,
                ComparisonVersion = CandidateComparisonAlgorithmVersion.Current,
            },
            cancellationToken: cancellationToken));
        return rows.ToDictionary(
            row => CandidatePairKey.Create(row.TrackIdA, row.TrackIdB),
            row => new DateTime(row.ComparedAtUtcTicks, DateTimeKind.Utc));
    }

    private async Task EnsureComparisonsInScopeAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlyCollection<CandidateComparison> comparisons,
        CancellationToken cancellationToken)
    {
        if (libraryId is null || comparisons.Count == 0)
        {
            return;
        }

        var trackIds = comparisons
            .SelectMany(item => new[] { item.TrackIdA, item.TrackIdB })
            .Distinct()
            .ToArray();
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM LibraryTracks WHERE LibraryId = @LibraryId AND TrackId IN @TrackIds;",
            new { LibraryId = libraryId, TrackIds = trackIds },
            transaction,
            cancellationToken: cancellationToken));
        if (count != trackIds.Length)
        {
            throw new InvalidOperationException("現在LibraryのMembership外TrackをCandidate Comparisonとして保存できません。");
        }
    }

    private static async Task UpsertCoreAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlyCollection<CandidateComparison> comparisons,
        CancellationToken cancellationToken)
    {
        if (comparisons.Count == 0)
        {
            return;
        }

        const string sql = """
            INSERT INTO CandidateComparisons (
                TrackIdA, TrackIdB, Similarity, BestOffsetItems, BestOffsetTicks,
                MatchedItems, MatchedDurationTicks, CoverageA, CoverageB, DurationRatio,
                ComparisonVersion, ComparedAtUtcTicks)
            VALUES (
                @TrackIdA, @TrackIdB, @Similarity, @BestOffsetItems, @BestOffsetTicks,
                @MatchedItems, @MatchedDurationTicks, @CoverageA, @CoverageB, @DurationRatio,
                @ComparisonVersion, @ComparedAtUtcTicks)
            ON CONFLICT (TrackIdA, TrackIdB) DO UPDATE SET
                Similarity = excluded.Similarity,
                BestOffsetItems = excluded.BestOffsetItems,
                BestOffsetTicks = excluded.BestOffsetTicks,
                MatchedItems = excluded.MatchedItems,
                MatchedDurationTicks = excluded.MatchedDurationTicks,
                CoverageA = excluded.CoverageA,
                CoverageB = excluded.CoverageB,
                DurationRatio = excluded.DurationRatio,
                ComparisonVersion = excluded.ComparisonVersion,
                ComparedAtUtcTicks = excluded.ComparedAtUtcTicks;
            """;
        var comparedAt = DateTime.UtcNow.Ticks;
        var parameters = comparisons.Select(item => new
        {
            item.TrackIdA,
            item.TrackIdB,
            item.Similarity,
            item.BestOffsetItems,
            BestOffsetTicks = item.BestOffset.Ticks,
            item.MatchedItems,
            MatchedDurationTicks = item.MatchedDuration.Ticks,
            item.CoverageA,
            item.CoverageB,
            item.DurationRatio,
            ComparisonVersion = CandidateComparisonAlgorithmVersion.Current,
            ComparedAtUtcTicks = comparedAt,
        });
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    private static CandidateComparison ToDomain(ComparisonRow row)
        => new(
            row.TrackIdA,
            row.TrackIdB,
            row.Similarity,
            checked((int)row.BestOffsetItems),
            TimeSpan.FromTicks(row.BestOffsetTicks),
            checked((int)row.MatchedItems),
            TimeSpan.FromTicks(row.MatchedDurationTicks),
            row.CoverageA,
            row.CoverageB,
            row.DurationRatio);

    private sealed record ComparisonRow(
        long TrackIdA,
        long TrackIdB,
        double Similarity,
        long BestOffsetItems,
        long BestOffsetTicks,
        long MatchedItems,
        long MatchedDurationTicks,
        double CoverageA,
        double CoverageB,
        double DurationRatio);

    private sealed record ComparedAtRow(long TrackIdA, long TrackIdB, long ComparedAtUtcTicks);
}
