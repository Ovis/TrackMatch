namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// TrackMatchがユーザー単位で保持する永続データの標準配置先を提供する。
/// </summary>
public static class TrackMatchDataPaths
{
    /// <summary>
    /// 標準のSQLiteデータベースファイルパスを取得する。
    /// </summary>
    /// <remarks>
    /// GUIとScannerが同じDBを暗黙に利用できるよう、実行ファイルの配置場所ではなくLocalApplicationData配下へ固定する。
    /// </remarks>
    public static string DefaultDatabasePath
    {
        get
        {
            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new InvalidOperationException("LocalApplicationDataのパスを取得できない。");
            }

            return Path.Combine(localApplicationData, "TrackMatch", "trackmatch.db");
        }
    }
}
