using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 詳細比較前の候補TrackペアをSQLiteへ保存する。
/// </summary>
public sealed class SqliteCandidatePairRepository(
    SqliteDatabase database,
    long? libraryId = null) : ICandidatePairRepository
{
    public async Task ReplaceAllAsync(
        IReadOnlyCollection<CandidatePair> pairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string selectSql = """
            SELECT p.TrackIdA, p.TrackIdB, p.MinimumSegmentHashDistance
            FROM CandidatePairs p
            INNER JOIN Tracks a ON a.Id = p.TrackIdA
            INNER JOIN Tracks b ON b.Id = p.TrackIdB
            WHERE @LibraryId IS NULL
               OR (a.LibraryId = @LibraryId AND b.LibraryId = @LibraryId);
            """;
        var existingRows = (await connection.QueryAsync<CandidatePairRow>(new CommandDefinition(
            selectSql,
            new { LibraryId = libraryId },
            transaction,
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
        await EnsurePairsInScopeAsync(connection, transaction, changed, cancellationToken);
        await UpsertAsync(connection, transaction, changed, cancellationToken);

        // 不変ペアをDELETE/INSERTしないことで、その配下の詳細比較・分類結果を保持する。
        // Fingerprint自体が更新された場合の比較失効判定は比較日時とFingerprint抽出日時で行う。
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceForTracksAsync(
        IReadOnlyCollection<long> trackIds,
        IReadOnlyCollection<CandidatePair> pairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        ArgumentNullException.ThrowIfNull(pairs);
        if (trackIds.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsurePairsInScopeAsync(connection, transaction, pairs, cancellationToken);

        // 変更Track数がSQLiteのパラメータ上限を超えても処理できるよう、一時表を使って対象集合を渡す。
        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE TEMP TABLE IF NOT EXISTS AffectedCandidateTracks (TrackId INTEGER PRIMARY KEY);",
            transaction: transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM AffectedCandidateTracks;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO AffectedCandidateTracks (TrackId) VALUES (@TrackId);",
            trackIds.Distinct().Select(trackId => new { TrackId = trackId }),
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM CandidatePairs
            WHERE EXISTS (SELECT 1 FROM AffectedCandidateTracks a WHERE a.TrackId = CandidatePairs.TrackIdA)
               OR EXISTS (SELECT 1 FROM AffectedCandidateTracks a WHERE a.TrackId = CandidatePairs.TrackIdB);
            """,
            transaction: transaction,
            cancellationToken: cancellationToken));

        await UpsertAsync(connection, transaction, pairs, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        IReadOnlyCollection<CandidatePairKey> pairKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairKeys);
        if (pairKeys.Count == 0)
        {
            return;
        }

        const string sql = """
            DELETE FROM CandidatePairs
            WHERE TrackIdA = @TrackIdA
              AND TrackIdB = @TrackIdB
              AND (
                    @LibraryId IS NULL
                 OR (
                        TrackIdA IN (SELECT Id FROM Tracks WHERE LibraryId = @LibraryId)
                    AND TrackIdB IN (SELECT Id FROM Tracks WHERE LibraryId = @LibraryId)));
            """;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            pairKeys.Select(key => new { key.TrackIdA, key.TrackIdB, LibraryId = libraryId }),
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<CandidatePair>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT p.TrackIdA, p.TrackIdB, p.MinimumSegmentHashDistance
            FROM CandidatePairs p
            INNER JOIN Tracks a ON a.Id = p.TrackIdA
            INNER JOIN Tracks b ON b.Id = p.TrackIdB
            WHERE @LibraryId IS NULL
               OR (a.LibraryId = @LibraryId AND b.LibraryId = @LibraryId)
            ORDER BY p.TrackIdA, p.TrackIdB;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<CandidatePairRow>(new CommandDefinition(
            sql,
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken));
        return rows
            .Select(row => new CandidatePair(
                row.TrackIdA,
                row.TrackIdB,
                checked((int)row.MinimumSegmentHashDistance)))
            .ToArray();
    }

    private async Task EnsurePairsInScopeAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<CandidatePair> pairs,
        CancellationToken cancellationToken)
    {
        if (libraryId is null || pairs.Count == 0)
        {
            return;
        }

        var trackIds = pairs.SelectMany(pair => new[] { pair.TrackIdA, pair.TrackIdB }).Distinct().ToArray();
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Tracks WHERE LibraryId = @LibraryId AND Id IN @TrackIds;",
            new { LibraryId = libraryId, TrackIds = trackIds },
            transaction,
            cancellationToken: cancellationToken));
        if (count != trackIds.Length)
        {
            throw new InvalidOperationException("異なるLibraryのTrackをCandidate Pairとして保存できません。");
        }
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<CandidatePair> pairs,
        CancellationToken cancellationToken)
    {
        if (pairs.Count == 0)
        {
            return;
        }

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
            pairs.Select(pair => new
            {
                pair.TrackIdA,
                pair.TrackIdB,
                pair.MinimumSegmentHashDistance,
                GeneratedAtUtcTicks = generatedAt,
            }),
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed record CandidatePairRow(
        long TrackIdA,
        long TrackIdB,
        long MinimumSegmentHashDistance);
}
