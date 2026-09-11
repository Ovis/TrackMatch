using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 候補Trackペアの詳細Fingerprint比較結果をSQLiteへ保存する。
/// </summary>
public sealed class SqliteCandidateComparisonRepository(SqliteDatabase database) : ICandidateComparisonRepository
{
    public async Task ReplaceAllAsync(
        IReadOnlyCollection<CandidateComparison> comparisons,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparisons);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateComparisons;",
            transaction: transaction,
            cancellationToken: cancellationToken));
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
            SELECT TrackIdA, TrackIdB, Similarity, BestOffsetItems, BestOffsetTicks,
                   MatchedItems, MatchedDurationTicks, CoverageA, CoverageB, DurationRatio
            FROM CandidateComparisons
            ORDER BY Similarity DESC, TrackIdA, TrackIdB;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ComparisonRow>(new CommandDefinition(
            sql,
            cancellationToken: cancellationToken));
        return rows.Select(ToDomain).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<CandidatePairKey, DateTime>> GetComparedAtUtcAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TrackIdA, TrackIdB, ComparedAtUtcTicks
            FROM CandidateComparisons;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ComparedAtRow>(new CommandDefinition(
            sql,
            cancellationToken: cancellationToken));
        return rows.ToDictionary(
            row => CandidatePairKey.Create(row.TrackIdA, row.TrackIdB),
            row => new DateTime(row.ComparedAtUtcTicks, DateTimeKind.Utc));
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
