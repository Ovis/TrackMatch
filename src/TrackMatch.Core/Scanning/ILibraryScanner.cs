namespace TrackMatch.Core.Scanning;

/// <summary>
/// 音源ライブラリを走査する。
/// </summary>
public interface ILibraryScanner
{
    IEnumerable<LibraryScanResult> Scan(string rootPath, CancellationToken cancellationToken = default);
}
