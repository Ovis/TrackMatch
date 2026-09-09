namespace TrackMatch.Core.Persistence;

/// <summary>
/// ライブラリ走査セッションの永続化境界を定義する。
/// </summary>
public interface IScanSessionRepository
{
    Task<long> StartAsync(string rootPath, DateTime startedAtUtc, CancellationToken cancellationToken = default);

    Task CompleteAsync(
        long sessionId,
        DateTime completedAtUtc,
        ScanSessionSummary summary,
        CancellationToken cancellationToken = default);

    Task FailAsync(
        long sessionId,
        DateTime completedAtUtc,
        ScanSessionSummary summary,
        CancellationToken cancellationToken = default);
}
