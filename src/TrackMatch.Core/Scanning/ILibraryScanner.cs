namespace TrackMatch.Core.Scanning;

/// <summary>
/// 音源ライブラリを走査する。
/// </summary>
public interface ILibraryScanner
{
    /// <summary>
    /// 走査対象として扱う音声ファイル数を取得する。
    /// </summary>
    /// <remarks>
    /// 件数を事前取得できない実装ではnullを返してよい。進捗表示のための補助情報なので、
    /// 件数取得のためにメタデータ解析そのものを二重実行しないことを前提とする。
    /// </remarks>
    int? GetSupportedFileCount(string rootPath, CancellationToken cancellationToken = default) => null;

    /// <summary>
    /// 対象フォルダ以下の音声ファイルを走査する。
    /// </summary>
    IEnumerable<LibraryScanResult> Scan(string rootPath, CancellationToken cancellationToken = default);
}
