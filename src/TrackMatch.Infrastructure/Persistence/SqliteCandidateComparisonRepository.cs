using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 候補Trackペアの詳細Fingerprint比較結果をSQLiteへ保存する。
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
                WHERE TrackIdA IN (SELECT Id FROM Tracks WHERE LibraryId = @LibraryId)
                  AND TrackIdB IN (SELECT Id FROM Tracks WHERE LibraryId = @LibraryId);
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

        // 詳細比較値が変わったペアの分類結果は古くなるため、再分類されるまで表示対象に残さない。
        const string deleteClassificationSql = """
            DELETE FROM CandidateClassifications
            WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;
            """;
        await connection.ExecuteAsync(new CommandDefinition(
            deleteClassificationSql,
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
            INNER JOIN Tracks a ON a.Id = c.TrackIdA
            INNER JOIN Tracks b ON b.Id = c.TrackIdB
            WHERE @LibraryId IS NULL
               OR (a.LibraryId = @LibraryId AND b.LibraryId = @LibraryId)
            ORDER BY c.Similarity DESC, c.TrackIdA, c.TrackIdB;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ComparisonRow>(new CommandDefinition(
            sql,
            new { LibraryId = libraryId },
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
            INNER JOIN Tracks a ON a.Id = c.TrackIdA
            INNER JOIN Tracks b ON b.Id = c.TrackIdB
            WHERE @LibraryId IS NULL
               OR (a.LibraryId = @LibraryId AND b.LibraryId = @LibraryId);
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ComparedAtRow>(new CommandDefinition(
            sql,
            new { LibraryId = libraryId },
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
            "SELECT COUNT(*) FROM Tracks WHERE LibraryId = @LibraryId AND Id IN @TrackIds;",
            new { LibraryId = libraryId, TrackIds = trackIds },
            transaction,
            cancellationToken: cancellationToken));
        if (count != trackIds.Length)
        {
            throw new InvalidOperationException("異なるLibraryのTrackをCandidate Comparisonとして保存できません。");
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
                ComparedAtUtcTicks)
            VALUES (
                @TrackIdA, @TrackIdB, @Similarity, @BestOffsetItems, @BestOffsetTicks,
                @MatchedItems, @MatchedDurationTicks, @CoverageA, @CoverageB, @DurationRatio,
                @ComparedAtUtcTicks)
            ON CONFLICT (TrackIdA, TrackIdB) DO UPDATE SET
                Similarity = excluded.Similarity,
                BestOffsetItems = excluded.BestOffsetItems,
                BestOffsetTicks = excluded.BestOffsetTicks,
                MatchedItems = excluded.MatchedItems,
                MatchedDurationTicks = excluded.MatchedDurationTicks,
                CoverageA = excluded.CoverageA,
                CoverageB = excluded.CoverageB,
                DurationRatio = excluded.DurationRatio,
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
