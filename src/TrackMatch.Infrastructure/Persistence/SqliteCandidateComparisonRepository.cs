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

        const string insertSql = """
            INSERT INTO CandidateComparisons (
                TrackIdA, TrackIdB, Similarity, BestOffsetItems, BestOffsetTicks,
                MatchedItems, MatchedDurationTicks, CoverageA, CoverageB, DurationRatio,
                ComparedAtUtcTicks)
            VALUES (
                @TrackIdA, @TrackIdB, @Similarity, @BestOffsetItems, @BestOffsetTicks,
                @MatchedItems, @MatchedDurationTicks, @CoverageA, @CoverageB, @DurationRatio,
                @ComparedAtUtcTicks);
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateComparisons;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        if (comparisons.Count != 0)
        {
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
                insertSql,
                parameters,
                transaction,
                cancellationToken: cancellationToken));
        }

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
        return rows.Select(row => new CandidateComparison(
            row.TrackIdA,
            row.TrackIdB,
            row.Similarity,
            checked((int)row.BestOffsetItems),
            TimeSpan.FromTicks(row.BestOffsetTicks),
            checked((int)row.MatchedItems),
            TimeSpan.FromTicks(row.MatchedDurationTicks),
            row.CoverageA,
            row.CoverageB,
            row.DurationRatio)).ToArray();
    }

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
}
