using System.Text.Json;
using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Global Comparisonを、人手レビュー状態とLibrary Projectionを含めてGUI用に読み出す。
/// </summary>
public sealed class SqliteCandidateReviewReportRepository(SqliteDatabase database)
{
    /// <summary>
    /// 指定LibraryのMembershipに両Trackが属する詳細比較結果を取得する。
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
                   x.ComparedAtUtcTicks,
                   a.Path AS PathA, b.Path AS PathB,
                   a.ArtistsJson AS ArtistsJsonA, b.ArtistsJson AS ArtistsJsonB,
                   a.Title AS TitleA, b.Title AS TitleB,
                   a.Album AS AlbumA, b.Album AS AlbumB,
                   a.GenresJson AS GenresJsonA, b.GenresJson AS GenresJsonB,
                   a.Year AS YearA, b.Year AS YearB,
                   a.DurationTicks AS DurationTicksA, b.DurationTicks AS DurationTicksB,
                   a.FileSize AS FileSizeA, b.FileSize AS FileSizeB,
                   a.Format AS FormatA, b.Format AS FormatB,
                   a.Codec AS CodecA, b.Codec AS CodecB,
                   a.BitrateKbps AS BitrateKbpsA, b.BitrateKbps AS BitrateKbpsB,
                   a.SampleRateHz AS SampleRateHzA, b.SampleRateHz AS SampleRateHzB,
                   a.BitDepth AS BitDepthA, b.BitDepth AS BitDepthB,
                   a.Channels AS ChannelsA, b.Channels AS ChannelsB,
                   r.Decision AS ReviewDecision, s.KeepTrackId,
                   r.ReviewedAtUtcTicks,
                   r.SourceLibraryId AS ReviewSourceLibraryId,
                   rl.Name AS CurrentReviewSourceLibraryName,
                   r.SourceLibraryNameSnapshot AS ReviewSourceLibraryNameSnapshot
            FROM CandidateComparisons x
            LEFT JOIN CandidateClassifications c
                ON c.TrackIdA = x.TrackIdA AND c.TrackIdB = x.TrackIdB
            LEFT JOIN CandidateReviews r
                ON r.TrackIdA = x.TrackIdA AND r.TrackIdB = x.TrackIdB
            LEFT JOIN CandidateReviewSelections s
                ON s.TrackIdA = x.TrackIdA AND s.TrackIdB = x.TrackIdB
            LEFT JOIN Libraries rl ON rl.Id = r.SourceLibraryId
            INNER JOIN Tracks a ON a.Id = x.TrackIdA
            INNER JOIN Tracks b ON b.Id = x.TrackIdB
            WHERE a.IsMissing = 0
              AND b.IsMissing = 0
              AND EXISTS (
                    SELECT 1 FROM LibraryTracks la
                    WHERE la.LibraryId = @LibraryId AND la.TrackId = x.TrackIdA)
              AND EXISTS (
                    SELECT 1 FROM LibraryTracks lb
                    WHERE lb.LibraryId = @LibraryId AND lb.TrackId = x.TrackIdB)
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
                throw new InvalidDataException($"未知の分類種別です: {row.Kind}");
            }

            kind = parsedKind;
        }

        CandidateReviewDecision? reviewDecision = null;
        if (row.ReviewDecision is not null)
        {
            if (!Enum.TryParse<CandidateReviewDecision>(row.ReviewDecision, out var parsedDecision))
            {
                throw new InvalidDataException($"未知の候補レビュー判定です: {row.ReviewDecision}");
            }

            reviewDecision = parsedDecision;
        }

        var reviewSourceLibraryName = row.CurrentReviewSourceLibraryName ?? row.ReviewSourceLibraryNameSnapshot;
        return new CandidateReviewReportRow(
            row.TrackIdA, row.TrackIdB, kind, row.Reason,
            row.Similarity, row.CoverageA, row.CoverageB, row.DurationRatio,
            TimeSpan.FromTicks(row.BestOffsetTicks), TimeSpan.FromTicks(row.MatchedDurationTicks),
            row.PathA, row.PathB, Deserialize(row.ArtistsJsonA), Deserialize(row.ArtistsJsonB),
            row.TitleA, row.TitleB, row.AlbumA, row.AlbumB,
            Deserialize(row.GenresJsonA), Deserialize(row.GenresJsonB),
            TimeSpan.FromTicks(row.DurationTicksA), TimeSpan.FromTicks(row.DurationTicksB),
            row.FileSizeA, row.FileSizeB, row.FormatA, row.FormatB, row.CodecA, row.CodecB,
            ToInt(row.BitrateKbpsA), ToInt(row.BitrateKbpsB), ToInt(row.SampleRateHzA), ToInt(row.SampleRateHzB),
            ToInt(row.BitDepthA), ToInt(row.BitDepthB), ToInt(row.ChannelsA), ToInt(row.ChannelsB),
            reviewDecision, row.KeepTrackId,
            ToUInt(row.YearA), ToUInt(row.YearB),
            row.ReviewSourceLibraryId, reviewSourceLibraryName,
            IsReReviewRecommended(
                reviewDecision,
                kind,
                row.ComparedAtUtcTicks,
                row.ReviewedAtUtcTicks));
    }

    /// <summary>
    /// レビュー後にMachine Resultが更新され、かつHuman Verdictと現在分類が明確に逆方向の場合だけ再確認対象にする。
    /// ユーザーが機械判定を意図的に覆した直後まで再確認扱いにしないため、時系列も判定条件へ含める。
    /// </summary>
    private static bool IsReReviewRecommended(
        CandidateReviewDecision? reviewDecision,
        AudioRelationshipKind? kind,
        long comparedAtUtcTicks,
        long? reviewedAtUtcTicks)
    {
        if (reviewDecision is null
            || reviewedAtUtcTicks is null
            || comparedAtUtcTicks <= reviewedAtUtcTicks.Value)
        {
            return false;
        }

        return reviewDecision switch
        {
            CandidateReviewDecision.NotDuplicate => kind == AudioRelationshipKind.DuplicateCandidate,
            CandidateReviewDecision.ConfirmedDuplicate => kind is AudioRelationshipKind.ShortVersionCandidate
                or AudioRelationshipKind.AlternateVersionCandidate,
            _ => false,
        };
    }

    private static int? ToInt(long? value) => value is null ? null : checked((int)value.Value);
    private static uint? ToUInt(long? value) => value is null ? null : checked((uint)value.Value);

    private static IReadOnlyList<string> Deserialize(string json)
        => JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidDataException("TrackメタデータJSONを復元できませんでした。");

    private sealed record ReportRow(
        long TrackIdA, long TrackIdB, string? Kind, string? Reason,
        double Similarity, double CoverageA, double CoverageB, double DurationRatio,
        long BestOffsetTicks, long MatchedDurationTicks, long ComparedAtUtcTicks,
        string PathA, string PathB, string ArtistsJsonA, string ArtistsJsonB,
        string? TitleA, string? TitleB, string? AlbumA, string? AlbumB,
        string GenresJsonA, string GenresJsonB,
        long? YearA, long? YearB,
        long DurationTicksA, long DurationTicksB, long FileSizeA, long FileSizeB,
        string? FormatA, string? FormatB, string? CodecA, string? CodecB,
        long? BitrateKbpsA, long? BitrateKbpsB, long? SampleRateHzA, long? SampleRateHzB,
        long? BitDepthA, long? BitDepthB, long? ChannelsA, long? ChannelsB,
        string? ReviewDecision, long? KeepTrackId, long? ReviewedAtUtcTicks,
        long? ReviewSourceLibraryId, string? CurrentReviewSourceLibraryName, string? ReviewSourceLibraryNameSnapshot);
}
