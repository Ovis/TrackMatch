namespace TrackMatch.Core.Scanning;

/// <summary>
/// 音源ライブラリを走査する。
/// </summary>
public interface ILibraryScanner
{
    /// <summary>
    /// 対象フォルダ以下の正式対応音声ファイルを1回列挙し、後続処理で再利用できるScan計画を作成する。
    /// </summary>
    /// <remarks>
    /// 総件数表示のためにファイルシステムを再走査しないよう、列挙結果自体を計画へ保持する。
    /// </remarks>
    LibraryScanPlan PrepareScan(string rootPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 準備済みの対象ファイルを走査する。
    /// </summary>
    IEnumerable<LibraryScanResult> Scan(
        LibraryScanPlan plan,
        Func<LibraryFileSnapshot, bool> shouldSkipMetadata,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 1回のファイルシステム列挙で確定したScan対象を保持する。
/// </summary>
/// <param name="RootPath">正規化済みの走査Root</param>
/// <param name="Paths">正式対応音声ファイルのパス一覧</param>
public sealed record LibraryScanPlan(
    string RootPath,
    IReadOnlyList<string> Paths)
{
    /// <summary>
    /// 今回のScanで処理する正確なファイル総数を取得する。
    /// </summary>
    public int TotalFiles => Paths.Count;
}
