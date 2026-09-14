using TrackMatch.Core.Persistence;

namespace TrackMatch.App;

/// <summary>
/// Global Duplicate Group詳細画面で1ファイル分のメタデータとLibrary所属を表示するモデル。
/// </summary>
public sealed record DuplicateGroupTrackViewModel(
    long TrackId,
    bool IsKeep,
    bool IsInCurrentLibrary,
    bool IsMissing,
    string Title,
    string Artist,
    string? Album,
    string MetadataSummary,
    string Duration,
    string Path)
{
    /// <summary>現在Libraryとの関係を表示する短いラベル。</summary>
    public string ScopeLabel => IsInCurrentLibrary ? "現在のLibrary" : "Library外";

    /// <summary>一覧上で表示する現在LibraryのDisposition。</summary>
    public string DispositionLabel => IsKeep ? "Keep" : IsInCurrentLibrary ? "対象" : "—";

    /// <summary>MissingでないGlobal Group構成Trackは、Library所属に関係なくKeepとして選択できる。</summary>
    public bool CanSelectAsKeep => !IsMissing && !IsKeep;

    /// <summary>
    /// Library外Trackの物理ファイルをユーザーが明示的にGlobal Trashできるかどうか。
    /// </summary>
    public bool CanTrashGlobally => !IsInCurrentLibrary && !IsMissing;

    /// <summary>保存済みGlobal Trackから詳細画面用モデルを生成する。</summary>
    public static DuplicateGroupTrackViewModel Create(StoredTrack track, bool isKeep, bool isInCurrentLibrary)
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
        if (track.IsMissing) details.Add("Missing");

        return new DuplicateGroupTrackViewModel(
            track.Id,
            isKeep,
            isInCurrentLibrary,
            track.IsMissing,
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
