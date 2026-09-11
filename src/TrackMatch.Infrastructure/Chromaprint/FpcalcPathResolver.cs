namespace TrackMatch.Infrastructure.Chromaprint;

/// <summary>
/// TrackMatchが利用するfpcalc実行ファイルの場所を解決する。
/// </summary>
public static class FpcalcPathResolver
{
    /// <summary>
    /// 明示指定がない場合に、配布物へ同梱したfpcalcを優先して探索する。
    /// </summary>
    /// <param name="requestedPath">コマンドライン引数や環境変数で明示されたパス。既定値の<c>fpcalc</c>は未指定として扱う</param>
    /// <param name="baseDirectory">同梱fpcalcを探索するアプリケーション基準ディレクトリ</param>
    /// <returns>Process.Startへ渡すfpcalcのパスまたはコマンド名</returns>
    public static string Resolve(string requestedPath, string? baseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);

        // 明示パスは利用者の意図を最優先する。単純な"fpcalc"だけは従来のPATHフォールバック指定として扱う。
        if (!string.Equals(requestedPath, "fpcalc", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(requestedPath, "fpcalc.exe", StringComparison.OrdinalIgnoreCase))
        {
            return requestedPath;
        }

        var root = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        var bundled = Path.Combine(root, "fpcalc", "fpcalc.exe");
        return File.Exists(bundled) ? bundled : requestedPath;
    }
}
