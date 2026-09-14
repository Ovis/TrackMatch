namespace TrackMatch.Application;

/// <summary>
/// 品質解析の非同期世代を識別するため、同一プロセス内で必ず単調増加するUTC時刻を発行する。
/// </summary>
internal static class QualityAnalysisGeneration
{
    private static long _lastTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// 前回値より必ず大きい品質解析世代時刻を生成する。
    /// </summary>
    internal static DateTime CreateTimestamp()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _lastTicks);

            // DateTime.UtcNowの分解能より短い間隔でRestartしても世代が衝突しないよう、
            // 実時刻と直前値+1tickの大きい方を採用する。
            var next = Math.Max(DateTime.UtcNow.Ticks, observed + 1);
            if (Interlocked.CompareExchange(ref _lastTicks, next, observed) == observed)
            {
                return new DateTime(next, DateTimeKind.Utc);
            }
        }
    }
}
