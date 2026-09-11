using System.IO;
using TrackMatch.Core.Candidates;

namespace TrackMatch.App;

/// <summary>
/// 候補一覧の表示に必要な値だけを整形する。
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

    public string ArtistA => string.Join("; ", Row.ArtistsA);

    public string ArtistB => string.Join("; ", Row.ArtistsB);

    public string AlbumA => Row.AlbumA ?? string.Empty;

    public string AlbumB => Row.AlbumB ?? string.Empty;

    public string GenreA => string.Join("; ", Row.GenresA);

    public string GenreB => string.Join("; ", Row.GenresB);

    public string Similarity => Row.Similarity.ToString("P2");

    public string CoverageA => Row.CoverageA.ToString("P2");

    public string CoverageB => Row.CoverageB.ToString("P2");

    public string DurationRatio => Row.DurationRatio.ToString("P2");
}
