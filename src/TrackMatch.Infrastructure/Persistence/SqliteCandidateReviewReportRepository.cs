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
    /// 指定Libraryの未レビュー詳細比較結果をTrackメタデータ付きで取得する。
    /// </summary>
    public async Task<IReadOnlyList<CandidateReviewReportRow>> GetAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        const string sql = """
            SELECT x.TrackIdA, x.TrackIdB, c.Kind, c.Reason,
                   x.Similarity, x.CoverageA, x.CoverageB, x.DurationRatio,
                   x.BestOffsetTicks, x.MatchedDurationTicks,
                   a.Path AS PathA, b.Path AS PathB,
                   a.ArtistsJson AS ArtistsJsonA, b.ArtistsJson AS ArtistsJsonB,
                   a.Title AS TitleA, b.Title AS TitleB,
                   a.Album AS AlbumA, b.Album AS AlbumB,
                   a.GenresJson AS GenresJsonA, b.GenresJson AS GenresJsonB,
                   a.DurationTicks AS DurationTicksA, b.DurationTicks AS DurationTicksB,
                   a.FileSize AS FileSizeA, b.FileSize AS FileSizeB,
                   a.Format AS FormatA, b.Format AS FormatB,
                   a.Codec AS CodecA, b.Codec AS CodecB,
                   a.BitrateKbps AS BitrateKbpsA, b.BitrateKbps AS BitrateKbpsB,
                   a.SampleRateHz AS SampleRateHzA, b.SampleRateHz AS SampleRateHzB,
                   a.BitDepth AS BitDepthA, b.BitDepth AS BitDepthB,
                   a.Channels AS ChannelsA, b.Channels AS ChannelsB
            FROM CandidateComparisons x
            LEFT JOIN CandidateClassifications c
                ON c.TrackIdA = x.TrackIdA AND c.TrackIdB = x.TrackIdB
            INNER JOIN Tracks a ON a.Id = x.TrackIdA
            INNER JOIN Tracks b ON b.Id = x.TrackIdB
            WHERE a.LibraryId = @LibraryId
              AND b.LibraryId = @LibraryId
              AND NOT EXISTS (
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
            new { LibraryId = libraryId },
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
            Deserialize(row.GenresJsonB),
            TimeSpan.FromTicks(row.DurationTicksA),
            TimeSpan.FromTicks(row.DurationTicksB),
            row.FileSizeA,
            row.FileSizeB,
            row.FormatA,
            row.FormatB,
            row.CodecA,
            row.CodecB,
            ToInt(row.BitrateKbpsA),
            ToInt(row.BitrateKbpsB),
            ToInt(row.SampleRateHzA),
            ToInt(row.SampleRateHzB),
            ToInt(row.BitDepthA),
            ToInt(row.BitDepthB),
            ToInt(row.ChannelsA),
            ToInt(row.ChannelsB));
    }

    private static int? ToInt(long? value) => value is null ? null : checked((int)value.Value);

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
        string GenresJsonB,
        long DurationTicksA,
        long DurationTicksB,
        long FileSizeA,
        long FileSizeB,
        string? FormatA,
        string? FormatB,
        string? CodecA,
        string? CodecB,
        long? BitrateKbpsA,
        long? BitrateKbpsB,
        long? SampleRateHzA,
        long? SampleRateHzB,
        long? BitDepthA,
        long? BitDepthB,
        long? ChannelsA,
        long? ChannelsB);
}
