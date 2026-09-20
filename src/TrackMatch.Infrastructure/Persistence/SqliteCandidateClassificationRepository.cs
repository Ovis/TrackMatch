using System.Text.Json;
using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Global Candidate ClassificationをSQLiteへ保存し、Library Membershipで絞り込んで読み出す。
/// </summary>
public sealed class SqliteCandidateClassificationRepository(
    SqliteDatabase database,
    long? libraryId = null) : ICandidateClassificationRepository
{
    public async Task ReplaceAllAsync(
        IReadOnlyCollection<CandidateClassification> classifications,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classifications);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureClassificationsInScopeAsync(connection, transaction, classifications, cancellationToken);

        var existingRows = (await connection.QueryAsync<StoredClassificationRow>(new CommandDefinition(
            """
            SELECT c.TrackIdA, c.TrackIdB, c.Kind, c.Reason, c.ThresholdProfileJson
            FROM CandidateClassifications c
            INNER JOIN Tracks ta ON ta.Id = c.TrackIdA AND ta.IsMissing = 0
            INNER JOIN Tracks tb ON tb.Id = c.TrackIdB AND tb.IsMissing = 0
            WHERE @LibraryId IS NULL
               OR (
                    EXISTS (SELECT 1 FROM LibraryTracks a WHERE a.LibraryId = @LibraryId AND a.TrackId = c.TrackIdA)
                AND EXISTS (SELECT 1 FROM LibraryTracks b WHERE b.LibraryId = @LibraryId AND b.TrackId = c.TrackIdB));
            """,
            new { LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        var existingByPair = existingRows.ToDictionary(row => CandidatePairKey.Create(row.TrackIdA, row.TrackIdB));
        var incomingByPair = classifications.ToDictionary(item => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB));

        // Missing Trackを含むClassificationは今回の再分類対象ではないためexistingRowsへ含めない。
        // Active Pairのうち今回の入力から消えたものだけを削除し、同Content復帰用Machine Cacheは保持する。
        var obsoletePairs = existingByPair.Keys.Where(key => !incomingByPair.ContainsKey(key)).ToArray();
        if (obsoletePairs.Length != 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM CandidateClassifications WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;",
                obsoletePairs.Select(key => new { key.TrackIdA, key.TrackIdB }),
                transaction,
                cancellationToken: cancellationToken));
        }

        // 同じ入力を再分類しただけでClassifiedAtを更新すると、Human Verdict後の通常Scanまで
        // 「Machine Resultが更新された」と誤認する。分類内容またはProfileが実際に変化したPairだけ更新する。
        var changed = classifications
            .Where(item =>
            {
                var key = CandidatePairKey.Create(item.TrackIdA, item.TrackIdB);
                return !existingByPair.TryGetValue(key, out var existing)
                    || !string.Equals(existing.Kind, item.Kind.ToString(), StringComparison.Ordinal)
                    || !string.Equals(existing.Reason, item.Reason, StringComparison.Ordinal)
                    || !string.Equals(existing.ThresholdProfileJson, item.ThresholdProfileJson, StringComparison.Ordinal);
            })
            .ToArray();

        if (changed.Length != 0)
        {
            const string upsertSql = """
                INSERT INTO CandidateClassifications (
                    TrackIdA, TrackIdB, Kind, Reason, ThresholdProfileJson, ClassifiedAtUtcTicks)
                VALUES (@TrackIdA, @TrackIdB, @Kind, @Reason, @ThresholdProfileJson, @ClassifiedAtUtcTicks)
                ON CONFLICT (TrackIdA, TrackIdB) DO UPDATE SET
                    Kind = excluded.Kind,
                    Reason = excluded.Reason,
                    ThresholdProfileJson = excluded.ThresholdProfileJson,
                    ClassifiedAtUtcTicks = excluded.ClassifiedAtUtcTicks;
                """;
            var classifiedAt = DateTime.UtcNow.Ticks;
            var parameters = changed.Select(item => new
            {
                item.TrackIdA,
                item.TrackIdB,
                Kind = item.Kind.ToString(),
                item.Reason,
                item.ThresholdProfileJson,
                ClassifiedAtUtcTicks = classifiedAt,
            });
            await connection.ExecuteAsync(new CommandDefinition(
                upsertSql,
                parameters,
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 現在Libraryで有効なClassificationが保存されているPairを取得する。
    /// </summary>
    public async Task<IReadOnlySet<CandidatePairKey>> GetClassifiedPairKeysAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT c.TrackIdA, c.TrackIdB
            FROM CandidateClassifications c
            INNER JOIN CandidateComparisons x
                ON x.TrackIdA = c.TrackIdA AND x.TrackIdB = c.TrackIdB
               AND x.ComparisonVersion = @ComparisonVersion
            INNER JOIN Tracks a ON a.Id = c.TrackIdA AND a.IsMissing = 0
            INNER JOIN Tracks b ON b.Id = c.TrackIdB AND b.IsMissing = 0
            WHERE @LibraryId IS NULL
               OR (
                    EXISTS (SELECT 1 FROM LibraryTracks la WHERE la.LibraryId = @LibraryId AND la.TrackId = c.TrackIdA)
                AND EXISTS (SELECT 1 FROM LibraryTracks lb WHERE lb.LibraryId = @LibraryId AND lb.TrackId = c.TrackIdB));
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PairRow>(new CommandDefinition(
            sql,
            new
            {
                LibraryId = libraryId,
                ComparisonVersion = CandidateComparisonAlgorithmVersion.Current,
            },
            cancellationToken: cancellationToken));
        return rows
            .Select(row => CandidatePairKey.Create(row.TrackIdA, row.TrackIdB))
            .ToHashSet();
    }

    public async Task<IReadOnlyList<CandidateClassificationReportRow>> GetReportAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT c.TrackIdA, c.TrackIdB, c.Kind, c.Reason,
                   x.Similarity, x.CoverageA, x.CoverageB, x.DurationRatio,
                   x.BestOffsetTicks, x.MatchedDurationTicks,
                   a.Path AS PathA, b.Path AS PathB,
                   a.ArtistsJson AS ArtistsJsonA, b.ArtistsJson AS ArtistsJsonB,
                   a.Title AS TitleA, b.Title AS TitleB,
                   a.Album AS AlbumA, b.Album AS AlbumB,
                   a.GenresJson AS GenresJsonA, b.GenresJson AS GenresJsonB
            FROM CandidateClassifications c
            INNER JOIN CandidateComparisons x
                ON x.TrackIdA = c.TrackIdA AND x.TrackIdB = c.TrackIdB
               AND x.ComparisonVersion = @ComparisonVersion
            INNER JOIN Tracks a ON a.Id = c.TrackIdA AND a.IsMissing = 0
            INNER JOIN Tracks b ON b.Id = c.TrackIdB AND b.IsMissing = 0
            WHERE (
                    @LibraryId IS NULL
                 OR (
                        EXISTS (SELECT 1 FROM LibraryTracks la WHERE la.LibraryId = @LibraryId AND la.TrackId = c.TrackIdA)
                    AND EXISTS (SELECT 1 FROM LibraryTracks lb WHERE lb.LibraryId = @LibraryId AND lb.TrackId = c.TrackIdB)))
              AND NOT EXISTS (
                SELECT 1
                FROM CandidateReviews r
                WHERE r.TrackIdA = c.TrackIdA
                  AND r.TrackIdB = c.TrackIdB)
            ORDER BY CASE c.Kind
                WHEN 'DuplicateCandidate' THEN 0
                WHEN 'ShortVersionCandidate' THEN 1
                WHEN 'AlternateVersionCandidate' THEN 2
                ELSE 3 END,
                x.Similarity DESC, c.TrackIdA, c.TrackIdB;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReportRow>(new CommandDefinition(
            sql,
            new
            {
                LibraryId = libraryId,
                ComparisonVersion = CandidateComparisonAlgorithmVersion.Current,
            },
            cancellationToken: cancellationToken));
        return rows.Select(ToReport).ToArray();
    }

    private async Task EnsureClassificationsInScopeAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlyCollection<CandidateClassification> classifications,
        CancellationToken cancellationToken)
    {
        if (classifications.Count == 0)
        {
            return;
        }

        var trackIds = classifications
            .SelectMany(item => new[] { item.TrackIdA, item.TrackIdB })
            .Distinct()
            .ToArray();
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM Tracks t
            WHERE t.Id IN @TrackIds
              AND t.IsMissing = 0
              AND (
                    @LibraryId IS NULL
                 OR EXISTS (
                        SELECT 1 FROM LibraryTracks lt
                        WHERE lt.LibraryId = @LibraryId AND lt.TrackId = t.Id));
            """,
            new { LibraryId = libraryId, TrackIds = trackIds },
            transaction,
            cancellationToken: cancellationToken));
        if (count != trackIds.Length)
        {
            throw new InvalidOperationException("Missingまたは現在LibraryのMembership外TrackをCandidate Classificationとして保存できません。");
        }
    }

    private static CandidateClassificationReportRow ToReport(ReportRow row)
    {
        if (!Enum.TryParse<AudioRelationshipKind>(row.Kind, out var kind))
        {
            throw new InvalidDataException($"未知の分類種別です: {row.Kind}");
        }

        return new CandidateClassificationReportRow(
            row.TrackIdA,
            row.TrackIdB,
            kind,
            row.Reason,
            row.Similarity,
            row.CoverageA,
            row.CoverageB,
            row.DurationRatio,
            TimeSpan.FromTicks(row.BestOffsetTicks),
            TimeSpan.FromTicks(row.MatchedDurationTicks),
            row.PathA,
            row.PathB,
            Deserialize(row.ArtistsJsonA),
            Deserialize(row.ArtistsJsonB),
            row.TitleA,
            row.TitleB,
            row.AlbumA,
            row.AlbumB,
            Deserialize(row.GenresJsonA),
            Deserialize(row.GenresJsonB));
    }

    private static IReadOnlyList<string> Deserialize(string json)
        => JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidDataException("TrackメタデータJSONを復元できませんでした。");

    private sealed record StoredClassificationRow(
        long TrackIdA,
        long TrackIdB,
        string Kind,
        string Reason,
        string ThresholdProfileJson);

    private sealed record PairRow(long TrackIdA, long TrackIdB);

    private sealed record ReportRow(
        long TrackIdA,
        long TrackIdB,
        string Kind,
        string Reason,
        double Similarity,
        double CoverageA,
        double CoverageB,
        double DurationRatio,
        long BestOffsetTicks,
        long MatchedDurationTicks,
        string PathA,
        string PathB,
        string ArtistsJsonA,
        string ArtistsJsonB,
        string? TitleA,
        string? TitleB,
        string? AlbumA,
        string? AlbumB,
        string GenresJsonA,
        string GenresJsonB);
}
