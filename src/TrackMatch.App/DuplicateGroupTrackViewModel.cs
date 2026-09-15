using TrackMatch.Core.Persistence;

namespace TrackMatch.App;

/// <summary>
/// 重複グループ詳細画面で1ファイル分のメタデータとライブラリ所属を表示するモデル。
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
    string DetailMetadataSummary,
    string Duration,
    string Path)
{
    /// <summary>現在のライブラリとの関係を利用者向けに表示する短いラベル。</summary>
    public string ScopeLabel => IsInCurrentLibrary ? "現在のライブラリ" : "現在のライブラリ外";

    /// <summary>一覧上で表示する現在のライブラリでの扱い。</summary>
    public string DispositionLabel => IsKeep ? "残す" : "未設定";

    /// <summary>物理ファイルの存在状態を内部用語を使わず表示する。</summary>
    public string FileStateLabel => IsMissing ? "見つかりません" : "存在";

    /// <summary>見つからないファイル以外は、ライブラリ所属に関係なく残すファイルとして選択できる。</summary>
    public bool CanSelectAsKeep => !IsMissing && !IsKeep;

    /// <summary>
    /// 現在のライブラリ外にあり、かつ現在のライブラリで残す指定ではないファイルを、利用者が明示的にごみ箱へ移動できるかどうか。
    /// </summary>
    public bool CanTrashGlobally => !IsInCurrentLibrary && !IsMissing && !IsKeep;

    /// <summary>保存済みTrackから詳細画面用モデルを生成する。</summary>
    public static DuplicateGroupTrackViewModel Create(StoredTrack track, bool isKeep, bool isInCurrentLibrary)
    {
        ArgumentNullException.ThrowIfNull(track);
        var metadata = track.Metadata;
        var title = string.IsNullOrWhiteSpace(metadata.Title)
            ? System.IO.Path.GetFileName(metadata.Path)
            : metadata.Title;
        var artist = metadata.Artists.Count == 0 ? "アーティスト不明" : string.Join(" & ", metadata.Artists);

        // 一覧ではファイル同士を識別できれば十分なので、比較に使いやすい主要な形式情報だけに絞る。
        var listDetails = new List<string>();
        if (!string.IsNullOrWhiteSpace(metadata.Format)) listDetails.Add(metadata.Format);
        if (metadata.SampleRateHz is { } listSampleRate) listDetails.Add(FormatSampleRate(listSampleRate));
        if (metadata.BitDepth is { } listBitDepth) listDetails.Add($"{listBitDepth} bit");

        // 詳細側では従来の情報量を維持し、一覧を簡潔にした分の情報を失わないようにする。
        var detailItems = new List<string>();
        if (metadata.Year is { } year) detailItems.Add(year.ToString());
        if (!string.IsNullOrWhiteSpace(metadata.Format)) detailItems.Add(metadata.Format);
        if (metadata.SampleRateHz is { } sampleRate) detailItems.Add(FormatSampleRate(sampleRate));
        if (metadata.BitDepth is { } bitDepth) detailItems.Add($"{bitDepth} bit");
        if (metadata.BitrateKbps is { } bitrate) detailItems.Add($"{bitrate} kbps");
        if (metadata.Channels is { } channels) detailItems.Add($"{channels} ch");
        detailItems.Add(FormatFileSize(metadata.FileSize));

        return new DuplicateGroupTrackViewModel(
            track.Id,
            isKeep,
            isInCurrentLibrary,
            track.IsMissing,
            title,
            artist,
            metadata.Album,
            string.Join(" / ", listDetails),
            string.Join(" / ", detailItems),
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
