using System.Text.Json;
using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 詳細比較済み候補を、分類済み・未分類を問わずGUIレビュー用に読み出す。
/// </summary>
public sealed class SqliteCandidateReviewReportRepository(SqliteDatabase database)
{
    /// <summary>
    /// 未レビューの詳細比較結果をTrackメタデータ付きで取得する。
    /// </summary>
    public async Task<IReadOnlyList<CandidateReviewReportRow>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT x.TrackIdA, x.TrackIdB, c.Kind, c.Reason,
                   x.Similarity, x.CoverageA, x.CoverageB, x.DurationRatio,
                   x.BestOffsetTicks, x.MatchedDurationTicks,
                   a.Path AS PathA, b.Path AS PathB,
                   a.ArtistsJson AS ArtistsJsonA, b.ArtistsJson AS ArtistsJsonB,
                   a.Title AS TitleA, b.Title AS TitleB,
                   a.Album AS AlbumA, b.Album AS AlbumB,
                   a.GenresJson AS GenresJsonA, b.GenresJson AS GenresJsonB
            FROM CandidateComparisons x
            LEFT JOIN CandidateClassifications c
                ON c.TrackIdA = x.TrackIdA AND c.TrackIdB = x.TrackIdB
            INNER JOIN Tracks a ON a.Id = x.TrackIdA
            INNER JOIN Tracks b ON b.Id = x.TrackIdB
            WHERE NOT EXISTS (
                SELECT 1
                FROM CandidateReviews r
                WHERE r.TrackIdA = x.TrackIdA
                  AND r.TrackIdB = x.TrackIdB)
            ORDER BY CASE c.Kind
                WHEN 'DuplicateCandidate' THEN 0
                WHEN 'ShortVersionCandidate' THEN 1
                WHEN 'AlternateVersionCandidate' THEN 2
                WHEN 'NeedsReview' THEN 3
                ELSE 4 END,
                x.Similarity DESC, x.TrackIdA, x.TrackIdB;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReportRow>(new CommandDefinition(
            sql,
            cancellationToken: cancellationToken));
        return rows.Select(ToReport).ToArray();
    }

    private static CandidateReviewReportRow ToReport(ReportRow row)
    {
        AudioRelationshipKind? kind = null;
        if (row.Kind is not null)
        {
            if (!Enum.TryParse<AudioRelationshipKind>(row.Kind, out var parsedKind))
            {
                throw new InvalidDataException($"未知の分類種別である: {row.Kind}");
            }

            kind = parsedKind;
        }

        return new CandidateReviewReportRow(
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
        string? Kind,
        string? Reason,
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
