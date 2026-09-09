using TrackMatch.Core.Models;

namespace TrackMatch.Core.Scanning;

/// <summary>
/// 1ファイル分の走査結果を表す。
/// </summary>
public sealed record LibraryScanResult(
    string Path,
    AudioTrackMetadata? Metadata,
    string? ErrorMessage)
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
}
