using Dapper;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// ライブラリ走査の開始・終了状態をSQLiteへ記録する。
/// </summary>
public sealed class SqliteScanSessionRepository(SqliteDatabase database) : IScanSessionRepository
{
    public async Task<long> StartAsync(
        string rootPath,
        DateTime startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        const string sql = """
            INSERT INTO ScanSessions (RootPath, StartedAtUtcTicks, Status)
            VALUES (@RootPath, @StartedAtUtcTicks, 'Running');
            SELECT last_insert_rowid();
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new
            {
                RootPath = Path.GetFullPath(rootPath),
                StartedAtUtcTicks = startedAtUtc.ToUniversalTime().Ticks,
            },
            cancellationToken: cancellationToken));
    }

    public Task CompleteAsync(
        long sessionId,
        DateTime completedAtUtc,
        ScanSessionSummary summary,
        CancellationToken cancellationToken = default)
        => FinishAsync(sessionId, completedAtUtc, summary, "Completed", cancellationToken);

    public Task FailAsync(
        long sessionId,
        DateTime completedAtUtc,
        ScanSessionSummary summary,
        CancellationToken cancellationToken = default)
        => FinishAsync(sessionId, completedAtUtc, summary, "Failed", cancellationToken);

    private async Task FinishAsync(
        long sessionId,
        DateTime completedAtUtc,
        ScanSessionSummary summary,
        string status,
        CancellationToken cancellationToken)
    {
        if (sessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(summary);

        const string sql = """
            UPDATE ScanSessions
            SET CompletedAtUtcTicks = @CompletedAtUtcTicks,
                Status = @Status,
                TotalFiles = @TotalFiles,
                ProcessedFiles = @ProcessedFiles,
                AddedFiles = @AddedFiles,
                UpdatedFiles = @UpdatedFiles,
                RemovedFiles = @RemovedFiles,
                ErrorCount = @ErrorCount
            WHERE Id = @Id AND Status = 'Running';
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = sessionId,
                CompletedAtUtcTicks = completedAtUtc.ToUniversalTime().Ticks,
                Status = status,
                summary.TotalFiles,
                summary.ProcessedFiles,
                summary.AddedFiles,
                summary.UpdatedFiles,
                summary.RemovedFiles,
                summary.ErrorCount,
            },
            cancellationToken: cancellationToken));

        if (affected != 1)
        {
            throw new InvalidOperationException("Running状態のScanSessionを更新できなかった。");
        }
    }
}
