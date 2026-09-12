using System.Globalization;
using System.Text;

namespace TrackMatch.App.Diagnostics;

/// <summary>
/// GUI起動直後の障害調査用に、Release環境でも確実に残る最小限のファイルログを出力する。
/// </summary>
internal static class AppFileLogger
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrackMatch",
        "logs");

    private static readonly string LogPath = Path.Combine(
        LogDirectory,
        $"trackmatch-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");

    /// <summary>
    /// 現在のプロセスで使用しているログファイルの絶対パスを取得する。
    /// </summary>
    internal static string CurrentLogPath => LogPath;

    /// <summary>
    /// 通常の診断メッセージを追記する。
    /// </summary>
    internal static void Info(string message)
        => Write("INFO", message, null);

    /// <summary>
    /// 例外情報をスタックトレースを含めて追記する。
    /// </summary>
    internal static void Error(string message, Exception exception)
        => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);

                var builder = new StringBuilder();
                builder.Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                builder.Append(" [");
                builder.Append(level);
                builder.Append("] [T");
                builder.Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
                builder.Append("] ");
                builder.AppendLine(message);

                if (exception is not null)
                {
                    builder.AppendLine(exception.ToString());
                }

                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // ログ出力失敗が本来の障害を覆い隠すと調査不能になるため、診断処理自身の例外は外へ出さない。
        }
    }
}
