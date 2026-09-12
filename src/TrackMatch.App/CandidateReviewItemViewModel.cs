using System.IO;
using TrackMatch.Core.Candidates;

namespace TrackMatch.App;

/// <summary>
/// 候補一覧と詳細表示に必要な値をUI向けに整形する。
/// </summary>
public sealed class CandidateReviewItemViewModel(CandidateReviewReportRow row)
{
    public CandidateReviewReportRow Row { get; } = row;

    public long TrackIdA => Row.TrackIdA;

    public long TrackIdB => Row.TrackIdB;

    public string Kind => Row.Kind?.ToString() ?? "未分類";

    public string Reason => Row.Reason ?? "しきい値プロファイル未適用";

    public string TitleA => Row.TitleA ?? Path.GetFileNameWithoutExtension(Row.PathA);

    public string TitleB => Row.TitleB ?? Path.GetFileNameWithoutExtension(Row.PathB);

    public string ArtistA => FormatList(Row.ArtistsA);

    public string ArtistB => FormatList(Row.ArtistsB);

    public string AlbumA => Row.AlbumA ?? "-";

    public string AlbumB => Row.AlbumB ?? "-";

    public string GenreA => FormatList(Row.GenresA);

    public string GenreB => FormatList(Row.GenresB);

    public string Similarity => Row.Similarity.ToString("P2");

    public string CoverageA => Row.CoverageA.ToString("P2");

    public string CoverageB => Row.CoverageB.ToString("P2");

    public string DurationRatio => Row.DurationRatio.ToString("P2");

    public string BestOffset => $"{Row.BestOffset.TotalSeconds:+0.000;-0.000;0.000} s";

    public string MatchedDuration => FormatDuration(Row.MatchedDuration);

    public string DurationA => FormatDuration(Row.DurationA);

    public string DurationB => FormatDuration(Row.DurationB);

    public string FormatCodecA => FormatCodec(Row.FormatA, Row.CodecA);

    public string FormatCodecB => FormatCodec(Row.FormatB, Row.CodecB);

    public string FileSizeA => FormatFileSize(Row.FileSizeA);

    public string FileSizeB => FormatFileSize(Row.FileSizeB);

    public string BitrateA => FormatUnit(Row.BitrateKbpsA, "kbps");

    public string BitrateB => FormatUnit(Row.BitrateKbpsB, "kbps");

    public string SampleRateA => FormatUnit(Row.SampleRateHzA, "Hz");

    public string SampleRateB => FormatUnit(Row.SampleRateHzB, "Hz");

    public string BitDepthA => FormatUnit(Row.BitDepthA, "bit");

    public string BitDepthB => FormatUnit(Row.BitDepthB, "bit");

    public string ChannelsA => Row.ChannelsA?.ToString() ?? "-";

    public string ChannelsB => Row.ChannelsB?.ToString() ?? "-";

    private static string FormatList(IReadOnlyList<string> values)
        => values.Count == 0 ? "-" : string.Join("; ", values);

    private static string FormatDuration(TimeSpan value)
        => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss\.fff") : value.ToString(@"m\:ss\.fff");

    private static string FormatCodec(string? format, string? codec)
    {
        var parts = new[] { format, codec }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return parts.Length == 0 ? "-" : string.Join(" / ", parts);
    }

    private static string FormatFileSize(long bytes)
        => bytes >= 1024L * 1024L * 1024L
            ? $"{bytes / (1024d * 1024d * 1024d):0.##} GiB"
            : $"{bytes / (1024d * 1024d):0.##} MiB";

    private static string FormatUnit(int? value, string unit)
        => value is null ? "-" : $"{value.Value:N0} {unit}";
}
