using TrackMatch.Core.Models;

namespace TrackMatch.Core.Scanning;

/// <summary>
/// 1ファイル分の走査結果を表す。
/// </summary>
public sealed record LibraryScanResult(
    string Path,
    AudioTrackMetadata? Metadata,
    string? ErrorMessage,
    LibraryFileSnapshot? Snapshot = null,
    bool MetadataSkipped = false)
{
    public bool IsSuccess => Metadata is not null;

    public static LibraryScanResult Success(AudioTrackMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new LibraryScanResult(metadata.Path, metadata, null);
    }

    public static LibraryScanResult Failure(string path, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new LibraryScanResult(path, null, errorMessage);
    }

    /// <summary>
    /// File属性が保存済み状態と一致したため、Metadata解析を省略した走査結果を生成する。
    /// </summary>
    public static LibraryScanResult SkippedMetadata(LibraryFileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new LibraryScanResult(snapshot.Path, null, null, snapshot, MetadataSkipped: true);
    }
}

/// <summary>
/// MetadataをDecodeせず取得できる、変更判定用のFile属性を表す。
/// </summary>
public sealed record LibraryFileSnapshot(string Path, long FileSize, DateTime LastWriteTimeUtc);
