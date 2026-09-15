using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 詳細比較前のGlobal Candidate PairをSQLiteへ保存する。
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
            INNER JOIN Tracks ta ON ta.Id = p.TrackIdA AND ta.IsMissing = 0
            INNER JOIN Tracks tb ON tb.Id = p.TrackIdB AND tb.IsMissing = 0
            WHERE @LibraryId IS NULL
               OR (
                    EXISTS (SELECT 1 FROM LibraryTracks a WHERE a.LibraryId = @LibraryId AND a.TrackId = p.TrackIdA)
                AND EXISTS (SELECT 1 FROM LibraryTracks b WHERE b.LibraryId = @LibraryId AND b.TrackId = p.TrackIdB));
            """;
        var existingRows = (await connection.QueryAsync<CandidatePairRow>(new CommandDefinition(
            selectSql,
            new { LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        var incomingByKey = pairs.ToDictionary(pair => CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB));
        var reviewedKeys = (await connection.QueryAsync<ReviewPairRow>(new CommandDefinition(
            "SELECT TrackIdA, TrackIdB FROM CandidateReviews;",
            transaction: transaction,
            cancellationToken: cancellationToken)))
            .Select(row => CandidatePairKey.Create(row.TrackIdA, row.TrackIdB))
            .ToHashSet();

        // Candidate GeneratorはHuman Verdictより低信頼の探索レイヤーである。
        // 新しい生成ロジックで候補から外れても、レビュー済みPairを削除してComparisonをCASCADE消去してはいけない。
        // Missingを含むPairも現在の候補探索では評価していないため、同Content復帰時のMachine Cache再利用に備えて保持する。
        var obsolete = existingRows
            .Where(row =>
            {
                var key = CandidatePairKey.Create(row.TrackIdA, row.TrackIdB);
                return !incomingByKey.ContainsKey(key) && !reviewedKeys.Contains(key);
            })
            .ToArray();
        if (obsolete.Length != 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM CandidatePairs WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;",
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

        if (libraryId is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM CandidatePairs
                WHERE (
                        EXISTS (SELECT 1 FROM AffectedCandidateTracks a WHERE a.TrackId = CandidatePairs.TrackIdA)
                     OR EXISTS (SELECT 1 FROM AffectedCandidateTracks a WHERE a.TrackId = CandidatePairs.TrackIdB))
                  AND EXISTS (
                        SELECT 1 FROM Tracks ta
                        WHERE ta.Id = CandidatePairs.TrackIdA AND ta.IsMissing = 0)
                  AND EXISTS (
                        SELECT 1 FROM Tracks tb
                        WHERE tb.Id = CandidatePairs.TrackIdB AND tb.IsMissing = 0)
                  AND NOT EXISTS (
                        SELECT 1 FROM CandidateReviews r
                        WHERE r.TrackIdA = CandidatePairs.TrackIdA
                          AND r.TrackIdB = CandidatePairs.TrackIdB);
                """,
                transaction: transaction,
                cancellationToken: cancellationToken));
        }
        else
        {
            // PairはGlobalなので、現在Libraryで評価可能なPairだけを置換する。
            // Shared Trackの別Library専用Pair、Missingを含む未評価Pair、Human Verdict済みPairを
            // 現在Libraryの増分生成で消してはいけない。
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM CandidatePairs
                WHERE (
                        EXISTS (SELECT 1 FROM AffectedCandidateTracks a WHERE a.TrackId = CandidatePairs.TrackIdA)
                     OR EXISTS (SELECT 1 FROM AffectedCandidateTracks a WHERE a.TrackId = CandidatePairs.TrackIdB))
                  AND EXISTS (
                        SELECT 1 FROM LibraryTracks la
                        WHERE la.LibraryId = @LibraryId AND la.TrackId = CandidatePairs.TrackIdA)
                  AND EXISTS (
                        SELECT 1 FROM LibraryTracks lb
                        WHERE lb.LibraryId = @LibraryId AND lb.TrackId = CandidatePairs.TrackIdB)
                  AND EXISTS (
                        SELECT 1 FROM Tracks ta
                        WHERE ta.Id = CandidatePairs.TrackIdA AND ta.IsMissing = 0)
                  AND EXISTS (
                        SELECT 1 FROM Tracks tb
                        WHERE tb.Id = CandidatePairs.TrackIdB AND tb.IsMissing = 0)
                  AND NOT EXISTS (
                        SELECT 1 FROM CandidateReviews r
                        WHERE r.TrackIdA = CandidatePairs.TrackIdA
                          AND r.TrackIdB = CandidatePairs.TrackIdB);
                """,
                new { LibraryId = libraryId },
                transaction,
                cancellationToken: cancellationToken));
        }

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
                        EXISTS (SELECT 1 FROM LibraryTracks a WHERE a.LibraryId = @LibraryId AND a.TrackId = CandidatePairs.TrackIdA)
                    AND EXISTS (SELECT 1 FROM LibraryTracks b WHERE b.LibraryId = @LibraryId AND b.TrackId = CandidatePairs.TrackIdB)));
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
            WHERE @LibraryId IS NULL
               OR (
                    EXISTS (SELECT 1 FROM LibraryTracks a WHERE a.LibraryId = @LibraryId AND a.TrackId = p.TrackIdA)
                AND EXISTS (SELECT 1 FROM LibraryTracks b WHERE b.LibraryId = @LibraryId AND b.TrackId = p.TrackIdB))
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
            "SELECT COUNT(*) FROM LibraryTracks WHERE LibraryId = @LibraryId AND TrackId IN @TrackIds;",
            new { LibraryId = libraryId, TrackIds = trackIds },
            transaction,
            cancellationToken: cancellationToken));
        if (count != trackIds.Length)
        {
            throw new InvalidOperationException("現在LibraryのMembership外TrackをCandidate Pairとして保存できません。");
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

    private sealed record ReviewPairRow(long TrackIdA, long TrackIdB);
}
