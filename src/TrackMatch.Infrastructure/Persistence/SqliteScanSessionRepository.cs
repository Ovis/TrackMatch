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

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScanSessions (RootPath, StartedAtUtcTicks, Status)
            VALUES ($rootPath, $startedAtUtcTicks, 'Running');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$rootPath", Path.GetFullPath(rootPath));
        command.Parameters.AddWithValue("$startedAtUtcTicks", startedAtUtc.ToUniversalTime().Ticks);

        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("ScanSessionのIDを取得できなかった。"));
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

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScanSessions
            SET CompletedAtUtcTicks = $completedAtUtcTicks,
                Status = $status,
                TotalFiles = $totalFiles,
                ProcessedFiles = $processedFiles,
                AddedFiles = $addedFiles,
                UpdatedFiles = $updatedFiles,
                RemovedFiles = $removedFiles,
                ErrorCount = $errorCount
            WHERE Id = $id AND Status = 'Running';
            """;
        command.Parameters.AddWithValue("$completedAtUtcTicks", completedAtUtc.ToUniversalTime().Ticks);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$totalFiles", summary.TotalFiles);
        command.Parameters.AddWithValue("$processedFiles", summary.ProcessedFiles);
        command.Parameters.AddWithValue("$addedFiles", summary.AddedFiles);
        command.Parameters.AddWithValue("$updatedFiles", summary.UpdatedFiles);
        command.Parameters.AddWithValue("$removedFiles", summary.RemovedFiles);
        command.Parameters.AddWithValue("$errorCount", summary.ErrorCount);
        command.Parameters.AddWithValue("$id", sessionId);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Running状態のScanSessionを更新できなかった。");
        }
    }
}
