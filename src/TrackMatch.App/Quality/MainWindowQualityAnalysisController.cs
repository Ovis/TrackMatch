using System.ComponentModel;
using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Quality;
using TrackMatch.Infrastructure.Audio;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App.Quality;

/// <summary>
/// Main WindowのCandidate表示と、Track/Candidate品質解析のバックグラウンド実行を接続する。
/// </summary>
public sealed class MainWindowQualityAnalysisController : IDisposable
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Action<string> _setStatusText;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _localCancellation;
    private Task? _sessionTask;
    private Task? _localTask;
    private TrackQualityAnalysisCoordinator? _trackCoordinator;
    private int _sessionGeneration;
    private int _localGeneration;
    private bool _disposed;

    /// <summary>UI状態と品質解析基盤を接続するControllerを生成する。</summary>
    public MainWindowQualityAnalysisController(MainWindowViewModel viewModel, Action<string> setStatusText)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _setStatusText = setStatusText ?? throw new ArgumentNullException(nameof(setStatusText));
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.ReviewStarting += ViewModel_ReviewStarting;
        _viewModel.ReviewCandidatesUpdated += ViewModel_ReviewCandidatesUpdated;
    }

    /// <summary>現在選択中Libraryのレビュー対象Candidateについて、既存処理を中断してバックグラウンド解析を開始する。</summary>
    public void Restart()
    {
        ThrowIfDisposed();
        Stop();
        var generation = ++_sessionGeneration;

        // Library Scan/再読込中に品質解析を並走させると、候補集合が確定する前のPathやCandidateを解析してしまう。
        // Main Window側の完了通知で改めてRestartされるため、この時点では待機に留める。
        if (_viewModel.IsLoading || _viewModel.IsAnalyzing)
        {
            SetStatusIfCurrent(generation, "音質解析: 待機中");
            return;
        }

        var libraryId = _viewModel.SelectedLibrary?.Id;
        if (libraryId is null)
        {
            SetStatusIfCurrent(generation, "音質解析: ライブラリ未選択");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _sessionTask = RunSessionAsync(libraryId.Value, generation, cancellation.Token);
        _ = ObserveSessionAsync(_sessionTask, cancellation, generation);
    }

    /// <summary>選択CandidateのA/Bだけをキャッシュ破棄して再解析し、その後通常バックグラウンド解析を再開する。</summary>
    public async Task ReanalyzeSelectedAsync()
    {
        ThrowIfDisposed();
        var selected = _viewModel.SelectedCandidate;
        if (selected is null)
        {
            return;
        }

        await StopAndWaitAsync();
        var generation = ++_sessionGeneration;
        selected.MarkQualityAnalyzing();
        SetStatusIfCurrent(generation, "音質解析中 0 / 2");
        var database = new SqliteDatabase(_viewModel.DatabasePath);
        await database.InitializeAsync();
        var trackRepository = new SqliteTrackQualityAnalysisRepository(database);
        var candidateRepository = new SqliteCandidateQualityComparisonRepository(database);
        await candidateRepository.DeleteAsync(selected.TrackIdA, selected.TrackIdB);
        await trackRepository.DeleteAsync(selected.TrackIdA);
        await trackRepository.DeleteAsync(selected.TrackIdB);
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            var coordinator = new TrackQualityAnalysisCoordinator(new NAudioTrackQualityAnalyzer(), trackRepository);
            if (IsCurrent(generation))
            {
                _trackCoordinator = coordinator;
            }

            var progress = new Progress<TrackQualityAnalysisProgress>(value =>
                SetStatusIfCurrent(generation, $"音質解析中 {value.CompletedCount} / {value.TotalCount}"));
            await coordinator.RunAsync(
                [
                    new TrackQualityAnalysisRequest(selected.TrackIdA, selected.Row.PathA),
                    new TrackQualityAnalysisRequest(selected.TrackIdB, selected.Row.PathB),
                ],
                progress,
                cancellation.Token);
            var candidateService = CreateCandidateService(trackRepository, candidateRepository);
            await candidateService.AnalyzeAsync(CreateRequest(selected.Row), force: true, cancellation.Token);
            await RefreshPresentationAsync(
                selected,
                trackRepository,
                candidateRepository,
                cancellation.Token,
                shouldApply: () => IsCurrent(generation) && _viewModel.IsCurrentCandidate(selected));
            SetStatusIfCurrent(generation, "音質解析: 選択中の候補を再解析しました");
        }
        catch (OperationCanceledException)
        {
            SetStatusIfCurrent(generation, "音質解析: キャンセルしました");
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _trackCoordinator = null;
            }

            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            cancellation.Dispose();
        }

        // 選択Candidateの局所解析完了をLibrary-wide Full Sessionの暗黙Triggerにしない。
    }

    /// <summary>実行中の品質解析へキャンセルを要求する。アプリ終了時は完了待ちを行わない。</summary>
    public void Stop()
    {
        // Cancelされた旧TaskのProgress/完了通知が次セッションへ遅れて到着しても、世代不一致でUIを更新させない。
        _sessionGeneration++;
        _cancellation?.Cancel();
        _trackCoordinator = null;
        CancelLocal();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.ReviewStarting -= ViewModel_ReviewStarting;
        _viewModel.ReviewCandidatesUpdated -= ViewModel_ReviewCandidatesUpdated;
        Stop();
        _cancellation = null;
        _localCancellation = null;
        _trackCoordinator = null;
    }

    private async Task RunSessionAsync(long libraryId, int generation, CancellationToken cancellationToken)
    {
        var database = new SqliteDatabase(_viewModel.DatabasePath);
        await database.InitializeAsync(cancellationToken);
        var minimumSimilarity = _viewModel.SimilarityDisplayLowerBoundPercent / 100d;
        var rows = (await new SqliteCandidateReviewReportRepository(database).GetAsync(libraryId, cancellationToken))
            .Where(row => row.Similarity >= minimumSimilarity)
            .ToArray();
        if (rows.Length == 0)
        {
            SetStatusIfCurrent(generation, "音質解析: 対象候補なし");
            return;
        }

        var trackRepository = new SqliteTrackQualityAnalysisRepository(database);
        var candidateRepository = new SqliteCandidateQualityComparisonRepository(database);
        var candidateService = CreateCandidateService(trackRepository, candidateRepository);
        await RefreshVisiblePresentationsAsync(trackRepository, candidateRepository, generation, cancellationToken);
        var coordinator = new TrackQualityAnalysisCoordinator(new NAudioTrackQualityAnalyzer(), trackRepository);
        if (IsCurrent(generation))
        {
            _trackCoordinator = coordinator;
        }

        var requests = TrackQualityAnalysisRequestFactory.FromCandidates(rows);
        var progress = new Progress<TrackQualityAnalysisProgress>(value =>
            SetStatusIfCurrent(generation, $"音質解析中 {value.CompletedCount} / {value.TotalCount}"));
        var trackTask = coordinator.RunAsync(requests, progress, cancellationToken);

        // 選択中Candidateだけは全Track完了を待たず、A/B両方が解析済みになった時点で先に比較する。
        while (!trackTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = _viewModel.SelectedCandidate;
            if (selected is not null)
            {
                coordinator.Prioritize(selected.TrackIdA, selected.TrackIdB);
                await TryAnalyzeAndRefreshAsync(
                    selected,
                    candidateService,
                    trackRepository,
                    candidateRepository,
                    generation,
                    cancellationToken);
            }

            var delay = Task.Delay(250, cancellationToken);
            await Task.WhenAny(trackTask, delay);
        }

        await trackTask;
        if (IsCurrent(generation))
        {
            _trackCoordinator = null;
        }

        var orderedRows = OrderSelectedFirst(rows);
        var completed = 0;
        foreach (var row in orderedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed++;
            SetStatusIfCurrent(generation, $"音質比較中 {completed} / {orderedRows.Count}");
            var result = await candidateService.AnalyzeAsync(CreateRequest(row), cancellationToken: cancellationToken);
            if (FindVisibleCandidate(row.TrackIdA, row.TrackIdB) is { } item)
            {
                await RefreshPresentationAsync(
                    item,
                    trackRepository,
                    candidateRepository,
                    cancellationToken,
                    result,
                    () => IsCurrent(generation) && _viewModel.IsCurrentCandidate(item));
            }
        }

        await RefreshVisiblePresentationsAsync(trackRepository, candidateRepository, generation, cancellationToken);
        SetStatusIfCurrent(generation, $"音質解析完了 {requests.Count}曲 / 比較 {orderedRows.Count}件");
    }

    private void ViewModel_ReviewStarting(object? sender, EventArgs e)
    {
        // ReviewはQuality終了を待たず、世代を進めて旧SessionのUI反映だけを無効化する。
        Stop();
    }

    private void ViewModel_ReviewCandidatesUpdated(object? sender, ReviewCandidatesUpdatedEventArgs e)
    {
        if (e.MissingQualityItems.Count == 0)
        {
            return;
        }

        StartLocalAnalysis(e.MissingQualityItems);
    }

    private void StartLocalAnalysis(IReadOnlyList<CandidateReviewItemViewModel> items)
    {
        CancelLocal();
        var generation = ++_localGeneration;
        var cancellation = new CancellationTokenSource();
        _localCancellation = cancellation;
        _localTask = RunLocalAnalysisAsync(items, generation, cancellation.Token);
        _ = ObserveLocalAnalysisAsync(_localTask, cancellation, generation);
    }

    private async Task RunLocalAnalysisAsync(
        IReadOnlyList<CandidateReviewItemViewModel> items,
        int generation,
        CancellationToken cancellationToken)
    {
        var currentItems = items.Where(_viewModel.IsCurrentCandidate).ToArray();
        if (currentItems.Length == 0)
        {
            return;
        }

        SetLocalStatusIfCurrent(generation, $"音質局所解析中 0 / {currentItems.Length}");
        var database = new SqliteDatabase(_viewModel.DatabasePath);
        await database.InitializeAsync(cancellationToken);
        var trackRepository = new SqliteTrackQualityAnalysisRepository(database);
        var candidateRepository = new SqliteCandidateQualityComparisonRepository(database);
        var coordinator = new TrackQualityAnalysisCoordinator(new NAudioTrackQualityAnalyzer(), trackRepository);
        await coordinator.RunAsync(
            TrackQualityAnalysisRequestFactory.FromCandidates(currentItems.Select(item => item.Row).ToArray()),
            cancellationToken: cancellationToken);

        var candidateService = CreateCandidateService(trackRepository, candidateRepository);
        var completed = 0;
        foreach (var item in currentItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_viewModel.IsCurrentCandidate(item))
            {
                continue;
            }

            await candidateService.AnalyzeAsync(CreateRequest(item.Row), cancellationToken: cancellationToken);
            completed++;
            SetLocalStatusIfCurrent(generation, $"音質局所解析中 {completed} / {currentItems.Length}");
        }

        var stillCurrent = currentItems.Where(_viewModel.IsCurrentCandidate).ToArray();
        await CandidateQualityHydrator.HydrateAsync(
            stillCurrent,
            trackRepository,
            candidateRepository,
            cancellationToken,
            _viewModel.IsCurrentCandidate);
        SetLocalStatusIfCurrent(generation, $"音質局所解析完了 {stillCurrent.Length}件");
    }

    private async Task ObserveLocalAnalysisAsync(
        Task task,
        CancellationTokenSource cancellation,
        int generation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetLocalStatusIfCurrent(generation, $"音質局所解析失敗: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_localCancellation, cancellation))
            {
                _localCancellation = null;
                _localTask = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelLocal()
    {
        _localGeneration++;
        _localCancellation?.Cancel();
    }

    private void SetLocalStatusIfCurrent(int generation, string status)
    {
        if (!_disposed && generation == _localGeneration)
        {
            _setStatusText(status);
        }
    }

    private async Task TryAnalyzeAndRefreshAsync(
        CandidateReviewItemViewModel item,
        CandidateQualityAnalysisService candidateService,
        SqliteTrackQualityAnalysisRepository trackRepository,
        SqliteCandidateQualityComparisonRepository candidateRepository,
        int generation,
        CancellationToken cancellationToken)
    {
        var result = await candidateService.AnalyzeAsync(CreateRequest(item.Row), cancellationToken: cancellationToken);
        await RefreshPresentationAsync(
            item,
            trackRepository,
            candidateRepository,
            cancellationToken,
            result,
            () => IsCurrent(generation) && _viewModel.IsCurrentCandidate(item));
    }

    private async Task RefreshVisiblePresentationsAsync(
        SqliteTrackQualityAnalysisRepository trackRepository,
        SqliteCandidateQualityComparisonRepository candidateRepository,
        int generation,
        CancellationToken cancellationToken)
    {
        foreach (var item in _viewModel.Candidates.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshPresentationAsync(
                item,
                trackRepository,
                candidateRepository,
                cancellationToken,
                shouldApply: () => IsCurrent(generation) && _viewModel.IsCurrentCandidate(item));
        }
    }

    private static async Task RefreshPresentationAsync(
        CandidateReviewItemViewModel item,
        SqliteTrackQualityAnalysisRepository trackRepository,
        SqliteCandidateQualityComparisonRepository candidateRepository,
        CancellationToken cancellationToken,
        CandidateQualityComparison? knownComparison = null,
        Func<bool>? shouldApply = null)
    {
        var analysisA = await trackRepository.GetAsync(item.TrackIdA, cancellationToken);
        var analysisB = await trackRepository.GetAsync(item.TrackIdB, cancellationToken);
        var comparison = knownComparison ?? await candidateRepository.GetAsync(item.TrackIdA, item.TrackIdB, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (shouldApply is not null && !shouldApply())
        {
            return;
        }

        item.ApplyQualityAnalysis(analysisA, analysisB, comparison);
    }

    private IReadOnlyList<CandidateReviewReportRow> OrderSelectedFirst(IReadOnlyList<CandidateReviewReportRow> rows)
    {
        var selected = _viewModel.SelectedCandidate;
        if (selected is null)
        {
            return rows;
        }

        return rows.OrderByDescending(row => row.TrackIdA == selected.TrackIdA && row.TrackIdB == selected.TrackIdB).ToArray();
    }

    private CandidateReviewItemViewModel? FindVisibleCandidate(long trackIdA, long trackIdB)
        => _viewModel.Candidates.FirstOrDefault(item => item.TrackIdA == trackIdA && item.TrackIdB == trackIdB);

    private static CandidateQualityAnalysisService CreateCandidateService(
        SqliteTrackQualityAnalysisRepository trackRepository,
        SqliteCandidateQualityComparisonRepository candidateRepository)
        => new(new NAudioCandidateQualityAnalyzer(), trackRepository, candidateRepository);

    private static CandidateQualityAnalysisRequest CreateRequest(CandidateReviewReportRow row)
        => new(
            new CandidateComparison(
                row.TrackIdA,
                row.TrackIdB,
                row.Similarity,
                0,
                row.BestOffset,
                0,
                row.MatchedDuration,
                row.CoverageA,
                row.CoverageB,
                row.DurationRatio),
            row.PathA,
            row.PathB);

    private async Task StopAndWaitAsync()
    {
        var task = _sessionTask;
        Stop();
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ObserveSessionAsync(Task task, CancellationTokenSource cancellation, int generation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            SetStatusIfCurrent(generation, "音質解析: キャンセルしました");
        }
        catch (Exception ex)
        {
            SetStatusIfCurrent(generation, $"音質解析失敗: {ex.Message}");
        }
        finally
        {
            // 旧セッションの完了が新セッションより遅れても、新セッションの参照を消さない。
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                _sessionTask = null;
                if (IsCurrent(generation))
                {
                    _trackCoordinator = null;
                }
            }

            // Restart後に旧CancellationTokenSourceがCurrentでなくなっていても、所有者はこのObserverなので必ず解放する。
            cancellation.Dispose();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedCandidate) && _viewModel.SelectedCandidate is { } selected)
        {
            _trackCoordinator?.Prioritize(selected.TrackIdA, selected.TrackIdB);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.SimilarityDisplayLowerBoundPercent))
        {
            // 下限は表示だけでなく解析対象も定義する。Scan/Reload中ならRestart自身が待機し、完了後にMain Windowから再開される。
            Restart();
        }
    }

    private bool IsCurrent(int generation) => !_disposed && generation == _sessionGeneration;

    private void SetStatusIfCurrent(int generation, string status)
    {
        if (IsCurrent(generation))
        {
            _setStatusText(status);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
