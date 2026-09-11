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

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string selectSql = """
            SELECT TrackIdA, TrackIdB, MinimumSegmentHashDistance
            FROM CandidatePairs;
            """;
        var existingRows = (await connection.QueryAsync<CandidatePairRow>(new CommandDefinition(
            selectSql,
            transaction: transaction,
            cancellationToken: cancellationToken))).ToArray();
        var incomingByKey = pairs.ToDictionary(
            pair => CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB));

        var obsolete = existingRows
            .Where(row => !incomingByKey.ContainsKey(CandidatePairKey.Create(row.TrackIdA, row.TrackIdB)))
            .ToArray();
        if (obsolete.Length != 0)
        {
            const string deleteSql = """
                DELETE FROM CandidatePairs
                WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;
                """;
            await connection.ExecuteAsync(new CommandDefinition(
                deleteSql,
                obsolete.Select(row => new { row.TrackIdA, row.TrackIdB }),
                transaction,
                cancellationToken: cancellationToken));
        }

        var existingByKey = existingRows.ToDictionary(
            row => CandidatePairKey.Create(row.TrackIdA, row.TrackIdB));
        var changed = pairs
            .Where(pair =>
            {
                var key = CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB);
                return !existingByKey.TryGetValue(key, out var existing)
                    || checked((int)existing.MinimumSegmentHashDistance) != pair.MinimumSegmentHashDistance;
            })
            .ToArray();
        if (changed.Length != 0)
        {
            const string upsertSql = """
                INSERT INTO CandidatePairs (
                    TrackIdA, TrackIdB, MinimumSegmentHashDistance, GeneratedAtUtcTicks)
                VALUES (
                    @TrackIdA, @TrackIdB, @MinimumSegmentHashDistance, @GeneratedAtUtcTicks)
                ON CONFLICT (TrackIdA, TrackIdB) DO UPDATE SET
                    MinimumSegmentHashDistance = excluded.MinimumSegmentHashDistance,
                    GeneratedAtUtcTicks = excluded.GeneratedAtUtcTicks;
                """;
            var generatedAt = DateTime.UtcNow.Ticks;
            await connection.ExecuteAsync(new CommandDefinition(
                upsertSql,
                changed.Select(pair => new
                {
                    pair.TrackIdA,
                    pair.TrackIdB,
                    pair.MinimumSegmentHashDistance,
                    GeneratedAtUtcTicks = generatedAt,
                }),
                transaction,
                cancellationToken: cancellationToken));
        }

        // 不変ペアをDELETE/INSERTしないことで、その配下の詳細比較・分類結果を保持する。
        // Fingerprint自体が更新された場合の比較失効判定は比較日時とFingerprint抽出日時で行う。
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CandidatePair>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TrackIdA, TrackIdB, MinimumSegmentHashDistance
            FROM CandidatePairs
            ORDER BY TrackIdA, TrackIdB;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<CandidatePairRow>(new CommandDefinition(
            sql,
            cancellationToken: cancellationToken));
        return rows
            .Select(row => new CandidatePair(
                row.TrackIdA,
                row.TrackIdB,
                checked((int)row.MinimumSegmentHashDistance)))
            .ToArray();
    }

    private sealed record CandidatePairRow(
        long TrackIdA,
        long TrackIdB,
        long MinimumSegmentHashDistance);
}
