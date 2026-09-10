using System.Globalization;
using System.Text;
using TrackMatch.Core.Candidates;

internal enum CandidateReportFormat
{
    Csv,
    Text,
}

internal static class CandidateReportWriter
{
    public static bool TryParseFormat(string value, out CandidateReportFormat format)
    {
        if (string.Equals(value, "csv", StringComparison.OrdinalIgnoreCase))
        {
            format = CandidateReportFormat.Csv;
            return true;
        }

        if (string.Equals(value, "text", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "txt", StringComparison.OrdinalIgnoreCase))
        {
            format = CandidateReportFormat.Text;
            return true;
        }

        format = default;
        return false;
    }

    public static async Task WriteAsync(
        IReadOnlyList<CandidateClassificationReportRow> rows,
        CandidateReportFormat format,
        string? outputPath)
    {
        using var writer = outputPath is null
            ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), leaveOpen: true)
            : new StreamWriter(outputPath, append: false, new UTF8Encoding(true));

        if (format == CandidateReportFormat.Csv)
        {
            WriteCsv(writer, rows);
        }
        else
        {
            WriteText(writer, rows);
        }

        await writer.FlushAsync();
    }

    private static void WriteCsv(TextWriter writer, IReadOnlyList<CandidateClassificationReportRow> rows)
    {
        writer.WriteLine("Kind,Similarity,CoverageA,CoverageB,DurationRatio,BestOffsetSeconds,MatchedDurationSeconds,ArtistA,TitleA,AlbumA,GenreA,PathA,ArtistB,TitleB,AlbumB,GenreB,PathB,Reason");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",",
                Csv(row.Kind.ToString()),
                Number(row.Similarity),
                Number(row.CoverageA),
                Number(row.CoverageB),
                Number(row.DurationRatio),
                Number(row.BestOffset.TotalSeconds),
                Number(row.MatchedDuration.TotalSeconds),
                Csv(string.Join("; ", row.ArtistsA)),
                Csv(row.TitleA ?? string.Empty),
                Csv(row.AlbumA ?? string.Empty),
                Csv(string.Join("; ", row.GenresA)),
                Csv(row.PathA),
                Csv(string.Join("; ", row.ArtistsB)),
                Csv(row.TitleB ?? string.Empty),
                Csv(row.AlbumB ?? string.Empty),
                Csv(string.Join("; ", row.GenresB)),
                Csv(row.PathB),
                Csv(row.Reason)));
        }
    }

    private static void WriteText(TextWriter writer, IReadOnlyList<CandidateClassificationReportRow> rows)
    {
        writer.WriteLine("Kind\tSimilarity\tCoverageA\tCoverageB\tDurationRatio\tBestOffsetSeconds\tMatchedDurationSeconds\tArtistA\tTitleA\tAlbumA\tGenreA\tPathA\tArtistB\tTitleB\tAlbumB\tGenreB\tPathB\tReason");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join('\t',
                row.Kind,
                Number(row.Similarity),
                Number(row.CoverageA),
                Number(row.CoverageB),
                Number(row.DurationRatio),
                Number(row.BestOffset.TotalSeconds),
                Number(row.MatchedDuration.TotalSeconds),
                Text(string.Join("; ", row.ArtistsA)),
                Text(row.TitleA ?? string.Empty),
                Text(row.AlbumA ?? string.Empty),
                Text(string.Join("; ", row.GenresA)),
                Text(row.PathA),
                Text(string.Join("; ", row.ArtistsB)),
                Text(row.TitleB ?? string.Empty),
                Text(row.AlbumB ?? string.Empty),
                Text(string.Join("; ", row.GenresB)),
                Text(row.PathB),
                Text(row.Reason)));
        }
    }

    private static string Number(double value)
        => value.ToString("F6", CultureInfo.InvariantCulture);

    private static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string Text(string value)
        => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
