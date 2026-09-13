using TrackMatch.Core.Persistence;

namespace TrackMatch.App;

/// <summary>
/// 重複グループ詳細画面で1ファイル分のメタデータを表示するモデル。
/// </summary>
public sealed record DuplicateGroupTrackViewModel(
    long TrackId,
    bool IsKeep,
    string Title,
    string Artist,
    string? Album,
    string MetadataSummary,
    string Duration,
    string Path)
{
    /// <summary>保存済みTrackから詳細画面用モデルを生成する。</summary>
    public static DuplicateGroupTrackViewModel Create(StoredTrack track, bool isKeep)
    {
        ArgumentNullException.ThrowIfNull(track);
        var metadata = track.Metadata;
        var title = string.IsNullOrWhiteSpace(metadata.Title)
            ? System.IO.Path.GetFileName(metadata.Path)
            : metadata.Title;
        var artist = metadata.Artists.Count == 0 ? "アーティスト不明" : string.Join(" & ", metadata.Artists);

        var details = new List<string>();
        if (metadata.Year is { } year) details.Add(year.ToString());
        if (!string.IsNullOrWhiteSpace(metadata.Format)) details.Add(metadata.Format);
        if (metadata.SampleRateHz is { } sampleRate) details.Add(FormatSampleRate(sampleRate));
        if (metadata.BitDepth is { } bitDepth) details.Add($"{bitDepth} bit");
        if (metadata.BitrateKbps is { } bitrate) details.Add($"{bitrate} kbps");
        if (metadata.Channels is { } channels) details.Add($"{channels} ch");
        details.Add(FormatFileSize(metadata.FileSize));

        return new DuplicateGroupTrackViewModel(
            track.Id,
            isKeep,
            title,
            artist,
            metadata.Album,
            string.Join(" / ", details),
            FormatDuration(metadata.Duration),
            metadata.Path);
    }

    private static string FormatFileSize(long bytes)
        => bytes >= 1024L * 1024L
            ? $"{bytes / (1024d * 1024d):0.0} MiB"
            : $"{bytes / 1024d:0.0} KiB";

    private static string FormatSampleRate(int sampleRate)
        => sampleRate % 1000 == 0
            ? $"{sampleRate / 1000} kHz"
            : $"{sampleRate / 1000d:0.0} kHz";

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");
}
