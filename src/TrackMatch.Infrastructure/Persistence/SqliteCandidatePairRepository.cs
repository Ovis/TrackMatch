using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 詳細比較前の候補TrackペアをSQLiteへ保存する。
/// </summary>
public sealed class SqliteCandidatePairRepository(SqliteDatabase database) : ICandidatePairRepository
{
    public async Task ReplaceAllAsync(
        IReadOnlyCollection<CandidatePair> pairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        const string insertSql = """
            INSERT INTO CandidatePairs (
                TrackIdA, TrackIdB, MinimumSegmentHashDistance, GeneratedAtUtcTicks)
            VALUES (
                @TrackIdA, @TrackIdB, @MinimumSegmentHashDistance, @GeneratedAtUtcTicks);
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidatePairs;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        if (pairs.Count != 0)
        {
            var generatedAt = DateTime.UtcNow.Ticks;
            var parameters = pairs.Select(pair => new
            {
                pair.TrackIdA,
                pair.TrackIdB,
                pair.MinimumSegmentHashDistance,
                GeneratedAtUtcTicks = generatedAt,
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
