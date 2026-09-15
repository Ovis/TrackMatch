using TrackMatch.Core.Persistence;
using TrackMatch.Core.Quality;

namespace TrackMatch.Application;

/// <summary>
/// Candidateに必要なTrack単体品質解析を、キャッシュ再利用・優先度制御・並列数制限付きで実行する。
/// </summary>
public sealed class TrackQualityAnalysisCoordinator
{
    private const int MaximumAutomaticWorkerCount = 4;
    private readonly ITrackQualityAnalyzer _analyzer;
    private readonly ITrackQualityAnalysisRepository _repository;
    private readonly int _workerCount;
    private readonly object _gate = new();
    private readonly Queue<long> _normalQueue = new();
    private readonly Queue<long> _priorityQueue = new();
    private readonly Dictionary<long, TrackQualityAnalysisRequest> _pending = [];
    private bool _isRunning;

    /// <summary>
    /// 品質解析Coordinatorを生成する。
    /// </summary>
    /// <param name="analyzer">Track単体の音質解析を行う実装</param>
    /// <param name="repository">Track品質解析キャッシュのRepository</param>
    /// <param name="workerCount">テストや将来設定用の明示ワーカー数。nullの場合はCPU数から自動決定する</param>
    public TrackQualityAnalysisCoordinator(
        ITrackQualityAnalyzer analyzer,
        ITrackQualityAnalysisRepository repository,
        int? workerCount = null)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _workerCount = workerCount ?? DetermineAutomaticWorkerCount(Environment.ProcessorCount);
        if (_workerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount));
        }
    }

    /// <summary>
    /// Candidateに必要なTrackを重複排除して解析する。
    /// </summary>
    public async Task RunAsync(
        IReadOnlyCollection<TrackQualityAnalysisRequest> requests,
        IProgress<TrackQualityAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var uniqueRequests = NormalizeRequests(requests);
        lock (_gate)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("同じCoordinatorで品質解析を同時実行できません。");
            }

            _isRunning = true;
            _normalQueue.Clear();
            _priorityQueue.Clear();
            _pending.Clear();
            foreach (var request in uniqueRequests)
            {
                _pending.Add(request.TrackId, request);
                _normalQueue.Enqueue(request.TrackId);
            }
        }

        var counters = new ProgressCounters(uniqueRequests.Count);

        try
        {
            // 現行バージョンの成功済みキャッシュはDecodeせず完了扱いにする。
            // Failedは次回起動や再スキャンで自然に再試行できるよう、ここでは再キュー対象のまま残す。
            foreach (var request in uniqueRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cached = await _repository.GetAsync(request.TrackId, cancellationToken);
                if (!CanReuse(cached) || !RemovePending(request.TrackId))
                {
                    continue;
                }

                counters.RecordCompleted(failed: false);
            }

            progress?.Report(counters.Snapshot());
            var workers = Enumerable.Range(0, Math.Min(_workerCount, PendingCount))
                .Select(_ => RunWorkerAsync(counters, progress, cancellationToken))
                .ToArray();
            await Task.WhenAll(workers);
        }
        finally
        {
            lock (_gate)
            {
                _isRunning = false;
                _normalQueue.Clear();
                _priorityQueue.Clear();
                _pending.Clear();
            }
        }
    }

    /// <summary>
    /// ユーザーが現在確認しているCandidateのTrackを、未処理なら次の空きワーカーへ優先投入する。
    /// </summary>
    public void Prioritize(params long[] trackIds)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        lock (_gate)
        {
            foreach (var trackId in trackIds)
            {
                if (_pending.ContainsKey(trackId))
                {
                    // 通常Queueから物理削除せず優先Queueにも積む。
                    // 先に取得した側が_pendingから削除するため、重複実行は発生しない。
                    _priorityQueue.Enqueue(trackId);
                }
            }
        }
    }

    internal static int DetermineAutomaticWorkerCount(int processorCount)
    {
        if (processorCount <= 0)
        {
            return 1;
        }

        // UIと同期再生へCPUを残すため1論理コアを予約し、初期実装では過剰並列を避けて4 workerを上限とする。
        return Math.Clamp(processorCount - 1, 1, MaximumAutomaticWorkerCount);
    }

    private int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    private async Task RunWorkerAsync(
        ProgressCounters counters,
        IProgress<TrackQualityAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (TryTake(out var request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var analyzingStartedAtUtc = QualityAnalysisGeneration.CreateTimestamp();

            // 開始時刻を所有権トークンとしてAnalyzing行へ保存する。旧セッションの完了/Cancelが
            // 新セッションやForce Reanalysis後の状態を上書きしないよう、最終更新はこのトークンで条件付きCommitする。
            await _repository.UpsertAsync(
                CreateState(
                    request.TrackId,
                    QualityAnalysisStatus.Analyzing,
                    analyzedAtUtc: analyzingStartedAtUtc),
                cancellationToken);

            TrackQualityAnalysis result;
            try
            {
                result = await _analyzer.AnalyzeAsync(request.TrackId, request.Path, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 新しいセッションが既に同Trackを解析中なら開始時刻が異なるため、その状態は削除しない。
                await _repository.DeleteAnalyzingAsync(
                    request.TrackId,
                    analyzingStartedAtUtc,
                    CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                // 1 Trackの予期しない解析失敗で残りのCandidate品質解析を止めない。
                // Failedもこの解析がまだCurrentを所有している場合だけ保存する。
                result = CreateState(request.TrackId, QualityAnalysisStatus.Failed, ex.Message);
            }

            bool committed;
            try
            {
                committed = await _repository.TryCompleteAnalyzingAsync(
                    result,
                    analyzingStartedAtUtc,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await _repository.DeleteAnalyzingAsync(
                    request.TrackId,
                    analyzingStartedAtUtc,
                    CancellationToken.None);
                throw;
            }

            // Force Reanalysis/Content Change/新セッションで所有権を失っていた場合は解析結果を捨てる。
            // Workerとしては処理済みなので進捗だけ完了させ、Current Stateには触れない。
            counters.RecordCompleted(committed && result.Status == QualityAnalysisStatus.Failed);
            progress?.Report(counters.Snapshot());
        }
    }

    private bool TryTake(out TrackQualityAnalysisRequest request)
    {
        lock (_gate)
        {
            while (_priorityQueue.Count > 0)
            {
                var trackId = _priorityQueue.Dequeue();
                if (_pending.Remove(trackId, out request!))
                {
                    return true;
                }
            }

            while (_normalQueue.Count > 0)
            {
                var trackId = _normalQueue.Dequeue();
                if (_pending.Remove(trackId, out request!))
                {
                    return true;
                }
            }
        }

        request = null!;
        return false;
    }

    private bool RemovePending(long trackId)
    {
        lock (_gate)
        {
            return _pending.Remove(trackId);
        }
    }

    private static bool CanReuse(TrackQualityAnalysis? cached)
        => cached is not null
            && cached.AnalysisVersion == QualityAnalysisVersions.TrackQualityAnalysis
            && cached.Status is QualityAnalysisStatus.Analyzed or QualityAnalysisStatus.Unsupported;

    private static IReadOnlyList<TrackQualityAnalysisRequest> NormalizeRequests(
        IReadOnlyCollection<TrackQualityAnalysisRequest> requests)
    {
        var normalized = new Dictionary<long, TrackQualityAnalysisRequest>();
        foreach (var request in requests)
        {
            if (request.TrackId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(requests), "TrackIdは正数である必要があります。");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
            var fullPath = Path.GetFullPath(request.Path);
            if (normalized.TryGetValue(request.TrackId, out var existing)
                && !string.Equals(existing.Path, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Track {request.TrackId} に複数のPathが指定されています。");
            }

            normalized[request.TrackId] = new TrackQualityAnalysisRequest(request.TrackId, fullPath);
        }

        return normalized.Values.ToArray();
    }

    private static TrackQualityAnalysis CreateState(
        long trackId,
        QualityAnalysisStatus status,
        string? failureReason = null,
        DateTime? analyzedAtUtc = null)
        => new(
            trackId,
            QualityAnalysisVersions.TrackQualityAnalysis,
            status,
            null,
            null,
            null,
            null,
            0,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            null,
            null,
            null,
            null,
            null,
            null,
            analyzedAtUtc,
            failureReason);

    /// <summary>
    /// 複数workerから更新される進捗件数を原子的に保持する。
    /// </summary>
    private sealed class ProgressCounters(int total)
    {
        private int _completed;
        private int _failed;

        public void RecordCompleted(bool failed)
        {
            if (failed)
            {
                Interlocked.Increment(ref _failed);
            }

            Interlocked.Increment(ref _completed);
        }

        public TrackQualityAnalysisProgress Snapshot()
            => new(
                Volatile.Read(ref _completed),
                total,
                Volatile.Read(ref _failed));
    }
}
