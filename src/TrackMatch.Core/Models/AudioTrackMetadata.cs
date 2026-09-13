namespace TrackMatch.Core.Models;

/// <summary>
/// 音源ファイルから取得した、比較処理と人手レビューの基礎となるメタデータを表す。
/// </summary>
public sealed record AudioTrackMetadata(
    string Path,
    long FileSize,
    DateTime LastWriteTimeUtc,
    TimeSpan Duration,
    IReadOnlyList<string> Artists,
    string? Title,
    string? Album,
    uint? TrackNumber,
    uint? DiscNumber,
    IReadOnlyList<string> Genres,
    string? Format = null,
    string? Codec = null,
    int? BitrateKbps = null,
    int? SampleRateHz = null,
    int? BitDepth = null,
    int? Channels = null,
    uint? Year = null);
