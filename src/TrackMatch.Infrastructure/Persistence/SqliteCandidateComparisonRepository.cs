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
}
