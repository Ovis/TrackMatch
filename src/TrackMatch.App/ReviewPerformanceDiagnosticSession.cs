using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App;

/// <summary>
/// 大規模LibraryでのReview遅延を一操作単位で集計する診断専用Session。
/// </summary>
/// <remarks>
/// 計測処理そのものが通常利用時の性能へ影響しないよう、Detailed Logging有効時だけ生成する。
/// この型は性能改善後に残す運用機能ではなく、ボトルネック特定のための一時的な計測基盤である。
/// </remarks>
internal sealed class ReviewPerformanceDiagnosticSession : IAsyncDisposable
{
    private const int DispatcherProbeIntervalMilliseconds = 100;
    private const double DispatcherDelayThresholdMilliseconds = 250;
    private readonly string _operationId;
    private readonly ILogger _logger;
    private readonly Stopwatch _operationStopwatch = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, DiagnosticAggregate> _aggregates = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _probeCancellation = new();
    private Task? _probeTask;
    private long _maximumDispatcherDelayTicks;
    private int _dispatcherDelayThresholdExceededCount;

    /// <summary>Review診断Sessionを開始する。</summary>
    internal ReviewPerformanceDiagnosticSession(string operationId, ILogger logger)
    {
        _operationId = operationId;
        _logger = logger;
    }

    /// <summary>
    /// Review開始時点のDB規模を取得する。
    /// </summary>
    /// <remarks>
    /// Snapshot取得時間はReview本体の性能値へ混ぜず、診断オーバーヘッドとして独立して記録する。
    /// </remarks>
    internal async Task CaptureDatasetSnapshotAsync(SqliteDatabase database, long libraryId)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await database.OpenConnectionAsync();

        var libraries = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM Libraries;");
        var tracks = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM Tracks;");
        var candidatePairs = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM CandidatePairs;");
        var supplementalPairs = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM CandidatePairs WHERE MinimumSegmentHashDistance < 0;");
        var comparisons = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM CandidateComparisons;");
        var classifications = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM CandidateClassifications;");
        var currentReviews = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM CandidateReviews;");
        var globalGroups = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM DuplicateGroups;");
        var currentLibraryGroups = await ExecuteCountAsync(
            connection,
            """
            SELECT COUNT(DISTINCT dgt.DuplicateGroupId)
            FROM DuplicateGroupTracks dgt
            WHERE EXISTS (
                SELECT 1 FROM LibraryTracks lt
                WHERE lt.LibraryId = $libraryId AND lt.TrackId = dgt.TrackId);
            """,
            libraryId);
        var currentLibraryPairs = await ExecuteCountAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM CandidatePairs p
            WHERE EXISTS (SELECT 1 FROM LibraryTracks a WHERE a.LibraryId = $libraryId AND a.TrackId = p.TrackIdA)
              AND EXISTS (SELECT 1 FROM LibraryTracks b WHERE b.LibraryId = $libraryId AND b.TrackId = p.TrackIdB);
            """,
            libraryId);

        _logger.LogDebug(
            "Review Dataset Snapshot OperationId={OperationId} LibraryId={LibraryId} Libraries={Libraries} Tracks={Tracks} CandidatePairs={CandidatePairs} SupplementalPairs={SupplementalPairs} Comparisons={Comparisons} Classifications={Classifications} CurrentReviews={CurrentReviews} GlobalGroups={GlobalGroups} CurrentLibraryGroups={CurrentLibraryGroups} CurrentLibraryPairs={CurrentLibraryPairs} SnapshotElapsedMs={SnapshotElapsedMs:F1}",
            _operationId, libraryId, libraries, tracks, candidatePairs, supplementalPairs, comparisons,
            classifications, currentReviews, globalGroups, currentLibraryGroups, currentLibraryPairs,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>RepositoryやSQL処理の呼出回数・時間・処理件数を集約する。</summary>
    internal void Record(string phase, TimeSpan elapsed, int? rowCount = null)
    {
        var aggregate = _aggregates.GetOrAdd(phase, static _ => new DiagnosticAggregate());
        aggregate.Add(elapsed, rowCount);
    }

    /// <summary>Track単位Repositoryアクセスを集約し、重複Lookupを判定可能にする。</summary>
    internal void RecordTrackLookup(string phase, long trackId, TimeSpan elapsed)
    {
        var aggregate = _aggregates.GetOrAdd(phase, static _ => new DiagnosticAggregate());
        aggregate.Add(elapsed, 1, trackId);
    }

    /// <summary>UI Dispatcherの応答遅延計測を開始する。</summary>
    internal void StartDispatcherProbe()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || _probeTask is not null)
        {
            return;
        }

        _probeTask = Task.Run(() => ProbeDispatcherAsync(dispatcher, _probeCancellation.Token));
    }

    /// <summary>Review操作全体の集約結果をDebugログへ出力する。</summary>
    internal async Task CompleteAsync(TimeSpan reviewElapsed)
    {
        _probeCancellation.Cancel();
        if (_probeTask is not null)
        {
            try
            {
                await _probeTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var entry in _aggregates.OrderByDescending(item => item.Value.TotalTicks))
        {
            var snapshot = entry.Value.Snapshot();
            _logger.LogDebug(
                "Review性能集約 OperationId={OperationId} Phase={Phase} CallCount={CallCount} TotalMs={TotalMs:F1} MaxMs={MaxMs:F1} RowCount={RowCount} UniqueTrackCount={UniqueTrackCount}",
                _operationId, entry.Key, snapshot.CallCount,
                TimeSpan.FromTicks(snapshot.TotalTicks).TotalMilliseconds,
                TimeSpan.FromTicks(snapshot.MaxTicks).TotalMilliseconds,
                snapshot.RowCount, snapshot.UniqueTrackCount);
        }

        _logger.LogDebug(
            "Review性能Summary OperationId={OperationId} ReviewElapsedMs={ReviewElapsedMs:F1} DiagnosticSessionElapsedMs={DiagnosticSessionElapsedMs:F1} MaxDispatcherDelayMs={MaxDispatcherDelayMs:F1} DispatcherDelayOverThresholdCount={DispatcherDelayOverThresholdCount} DispatcherDelayThresholdMs={DispatcherDelayThresholdMs:F0}",
            _operationId, reviewElapsed.TotalMilliseconds, _operationStopwatch.Elapsed.TotalMilliseconds,
            TimeSpan.FromTicks(Interlocked.Read(ref _maximumDispatcherDelayTicks)).TotalMilliseconds,
            Volatile.Read(ref _dispatcherDelayThresholdExceededCount),
            DispatcherDelayThresholdMilliseconds);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _probeCancellation.Cancel();
        if (_probeTask is not null)
        {
            try
            {
                await _probeTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _probeCancellation.Dispose();
    }

    private async Task ProbeDispatcherAsync(Dispatcher dispatcher, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(DispatcherProbeIntervalMilliseconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var queuedAt = Stopwatch.GetTimestamp();
            await dispatcher.InvokeAsync(
                () =>
                {
                    var delay = Stopwatch.GetElapsedTime(queuedAt);
                    UpdateMaximumDispatcherDelay(delay);
                    if (delay.TotalMilliseconds >= DispatcherDelayThresholdMilliseconds)
                    {
                        Interlocked.Increment(ref _dispatcherDelayThresholdExceededCount);
                        _logger.LogDebug(
                            "Review Dispatcher遅延 OperationId={OperationId} DelayMs={DelayMs:F1} ElapsedFromReviewStartMs={ElapsedFromReviewStartMs:F1}",
                            _operationId, delay.TotalMilliseconds, _operationStopwatch.Elapsed.TotalMilliseconds);
                    }
                },
                DispatcherPriority.Background,
                cancellationToken);
        }
    }

    private void UpdateMaximumDispatcherDelay(TimeSpan delay)
    {
        var candidate = delay.Ticks;
        while (true)
        {
            var current = Interlocked.Read(ref _maximumDispatcherDelayTicks);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maximumDispatcherDelayTicks, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static async Task<long> ExecuteCountAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql,
        long? libraryId = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (libraryId is not null)
        {
            command.Parameters.AddWithValue("$libraryId", libraryId.Value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class DiagnosticAggregate
    {
        private readonly object _sync = new();
        private readonly HashSet<long> _trackIds = [];
        private long _callCount;
        private long _totalTicks;
        private long _maxTicks;
        private long _rowCount;

        internal long TotalTicks
        {
            get
            {
                lock (_sync)
                {
                    return _totalTicks;
                }
            }
        }

        internal void Add(TimeSpan elapsed, int? rowCount, long? trackId = null)
        {
            lock (_sync)
            {
                _callCount++;
                _totalTicks += elapsed.Ticks;
                _maxTicks = Math.Max(_maxTicks, elapsed.Ticks);
                _rowCount += rowCount ?? 0;
                if (trackId is not null)
                {
                    _trackIds.Add(trackId.Value);
                }
            }
        }

        internal DiagnosticAggregateSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new DiagnosticAggregateSnapshot(
                    _callCount,
                    _totalTicks,
                    _maxTicks,
                    _rowCount,
                    _trackIds.Count);
            }
        }
    }

    private sealed record DiagnosticAggregateSnapshot(
        long CallCount,
        long TotalTicks,
        long MaxTicks,
        long RowCount,
        int UniqueTrackCount);
}
