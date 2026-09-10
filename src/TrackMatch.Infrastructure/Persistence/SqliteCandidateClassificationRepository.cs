using System.Text.Json;
using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 候補分類結果をSQLiteへ保存し、Trackメタデータ付きで読み出す。
/// </summary>
public sealed class SqliteCandidateClassificationRepository(SqliteDatabase database) : ICandidateClassificationRepository
{
    public async Task ReplaceAllAsync(
        IReadOnlyCollection<CandidateClassification> classifications,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classifications);

        const string insertSql = """
            INSERT INTO CandidateClassifications (
                TrackIdA, TrackIdB, Kind, Reason, ThresholdProfileJson, ClassifiedAtUtcTicks)
            VALUES (@TrackIdA, @TrackIdB, @Kind, @Reason, @ThresholdProfileJson, @ClassifiedAtUtcTicks);
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateClassifications;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        if (classifications.Count != 0)
        {
            var classifiedAt = DateTime.UtcNow.Ticks;
            var parameters = classifications.Select(item => new
            {
                item.TrackIdA,
                item.TrackIdB,
                Kind = item.Kind.ToString(),
                item.Reason,
                item.ThresholdProfileJson,
                ClassifiedAtUtcTicks = classifiedAt,
            });
            await connection.ExecuteAsync(new CommandDefinition(
                insertSql,
                parameters,
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
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
            INNER JOIN Tracks a ON a.Id = c.TrackIdA
            INNER JOIN Tracks b ON b.Id = c.TrackIdB
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
            cancellationToken: cancellationToken));
        return rows.Select(ToReport).ToArray();
    }

    private static CandidateClassificationReportRow ToReport(ReportRow row)
    {
        if (!Enum.TryParse<AudioRelationshipKind>(row.Kind, out var kind))
        {
            throw new InvalidDataException($"未知の分類種別である: {row.Kind}");
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
            ?? throw new InvalidDataException("TrackメタデータJSONを復元できなかった。");

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
