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
    private Task? _sessionTask;
    private TrackQualityAnalysisCoordinator? _trackCoordinator;
    private bool _disposed;

    /// <summary>
    /// UI状態と品質解析基盤を接続するControllerを生成する。
    /// </summary>
    public MainWindowQualityAnalysisController(MainWindowViewModel viewModel, Action<string> setStatusText)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _setStatusText = setStatusText ?? throw new ArgumentNullException(nameof(setStatusText));
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    /// <summary>
    /// 現在選択中Libraryの未レビューCandidateを対象に、既存処理を中断してバックグラウンド解析を開始する。
    /// </summary>
    public void Restart()
    {
        ThrowIfDisposed();
        Stop();

        var libraryId = _viewModel.SelectedLibrary?.Id;
        if (libraryId is null)
        {
            _setStatusText("音質解析: Library未選択");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _sessionTask = RunSessionAsync(libraryId.Value, cancellation.Token);
        _ = ObserveSessionAsync(_sessionTask, cancellation);
    }

    /// <summary>
    /// 選択CandidateのA/Bだけをキャッシュ破棄して再解析し、その後通常バックグラウンド解析を再開する。
    /// </summary>
    public async Task ReanalyzeSelectedAsync()
    {
        ThrowIfDisposed();
        var selected = _viewModel.SelectedCandidate;
        if (selected is null)
        {
            return;
        }

        await StopAndWaitAsync();
        selected.MarkQualityAnalyzing();
        _setStatusText("音質解析中 0 / 2");

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
            _trackCoordinator = coordinator;
            var progress = new Progress<TrackQualityAnalysisProgress>(value =>
                _setStatusText($"音質解析中 {value.CompletedCount} / {value.TotalCount}"));
            await coordinator.RunAsync(
                [
                    new TrackQualityAnalysisRequest(selected.TrackIdA, selected.Row.PathA),
                    new TrackQualityAnalysisRequest(selected.TrackIdB, selected.Row.PathB),
                ],
                progress,
                cancellation.Token);

            var candidateService = CreateCandidateService(trackRepository, candidateRepository);
            await candidateService.AnalyzeAsync(CreateRequest(selected.Row), force: true, cancellation.Token);
            await RefreshPresentationAsync(selected, trackRepository, candidateRepository, cancellation.Token);
            _setStatusText("音質解析: 選択Candidateの再解析完了");
        }
        catch (OperationCanceledException)
        {
            _setStatusText("音質解析: キャンセルしました");
        }
        finally
        {
            _trackCoordinator = null;
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            cancellation.Dispose();
        }

        if (!_disposed)
        {
            Restart();
        }
    }

    /// <summary>
    /// 実行中の品質解析へキャンセルを要求する。アプリ終了時は完了待ちを行わない。
    /// </summary>
    public void Stop() => _cancellation?.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Stop();
        _cancellation?.Dispose();
        _cancellation = null;
        _trackCoordinator = null;
    }

    private async Task RunSessionAsync(long libraryId, CancellationToken cancellationToken)
    {
        var database = new SqliteDatabase(_viewModel.DatabasePath);
        await database.InitializeAsync(cancellationToken);
        var rows = await new SqliteCandidateReviewReportRepository(database).GetAsync(libraryId, cancellationToken);
        if (rows.Count == 0)
        {
            _setStatusText("音質解析: 対象Candidateなし");
            return;
        }

        var trackRepository = new SqliteTrackQualityAnalysisRepository(database);
        var candidateRepository = new SqliteCandidateQualityComparisonRepository(database);
        var candidateService = CreateCandidateService(trackRepository, candidateRepository);

        await RefreshVisiblePresentationsAsync(trackRepository, candidateRepository, cancellationToken);

        var coordinator = new TrackQualityAnalysisCoordinator(new NAudioTrackQualityAnalyzer(), trackRepository);
        _trackCoordinator = coordinator;
        var requests = TrackQualityAnalysisRequestFactory.FromCandidates(rows);
        var progress = new Progress<TrackQualityAnalysisProgress>(value =>
            _setStatusText($"音質解析中 {value.CompletedCount} / {value.TotalCount}"));
        var trackTask = coordinator.RunAsync(requests, progress, cancellationToken);

        // 選択中Candidateだけは全Track完了を待たず、A/B両方が解析済みになった時点で先に比較する。
        while (!trackTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = _viewModel.SelectedCandidate;
            if (selected is not null)
            {
                coordinator.Prioritize(selected.TrackIdA, selected.TrackIdB);
                await TryAnalyzeAndRefreshAsync(selected, candidateService, trackRepository, candidateRepository, cancellationToken);
            }

            var delay = Task.Delay(250, cancellationToken);
            await Task.WhenAny(trackTask, delay);
        }

        await trackTask;
        _trackCoordinator = null;

        var orderedRows = OrderSelectedFirst(rows);
        var completed = 0;
        foreach (var row in orderedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed++;
            _setStatusText($"音質比較中 {completed} / {orderedRows.Count}");
            var result = await candidateService.AnalyzeAsync(CreateRequest(row), cancellationToken: cancellationToken);
            if (FindVisibleCandidate(row.TrackIdA, row.TrackIdB) is { } item)
            {
                await RefreshPresentationAsync(item, trackRepository, candidateRepository, cancellationToken, result);
            }
        }

        await RefreshVisiblePresentationsAsync(trackRepository, candidateRepository, cancellationToken);
        _setStatusText($"音質解析完了 {requests.Count}曲 / 比較 {orderedRows.Count}件");
    }

    private async Task TryAnalyzeAndRefreshAsync(
        CandidateReviewItemViewModel item,
        CandidateQualityAnalysisService candidateService,
        SqliteTrackQualityAnalysisRepository trackRepository,
        SqliteCandidateQualityComparisonRepository candidateRepository,
        CancellationToken cancellationToken)
    {
        var result = await candidateService.AnalyzeAsync(CreateRequest(item.Row), cancellationToken: cancellationToken);
        await RefreshPresentationAsync(item, trackRepository, candidateRepository, cancellationToken, result);
    }

    private async Task RefreshVisiblePresentationsAsync(
        SqliteTrackQualityAnalysisRepository trackRepository,
        SqliteCandidateQualityComparisonRepository candidateRepository,
        CancellationToken cancellationToken)
    {
        foreach (var item in _viewModel.Candidates.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshPresentationAsync(item, trackRepository, candidateRepository, cancellationToken);
        }
    }

    private static async Task RefreshPresentationAsync(
        CandidateReviewItemViewModel item,
        SqliteTrackQualityAnalysisRepository trackRepository,
        SqliteCandidateQualityComparisonRepository candidateRepository,
        CancellationToken cancellationToken,
        CandidateQualityComparison? knownComparison = null)
    {
        var analysisA = await trackRepository.GetAsync(item.TrackIdA, cancellationToken);
        var analysisB = await trackRepository.GetAsync(item.TrackIdB, cancellationToken);
        var comparison = knownComparison ?? await candidateRepository.GetAsync(item.TrackIdA, item.TrackIdB, cancellationToken);
        item.ApplyQualityAnalysis(analysisA, analysisB, comparison);
    }

    private IReadOnlyList<CandidateReviewReportRow> OrderSelectedFirst(IReadOnlyList<CandidateReviewReportRow> rows)
    {
        var selected = _viewModel.SelectedCandidate;
        if (selected is null)
        {
            return rows;
        }

        return rows
            .OrderByDescending(row => row.TrackIdA == selected.TrackIdA && row.TrackIdB == selected.TrackIdB)
            .ToArray();
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

    private async Task ObserveSessionAsync(Task task, CancellationTokenSource cancellation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                _setStatusText("音質解析: キャンセルしました");
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _setStatusText($"音質解析失敗: {ex.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                _sessionTask = null;
                _trackCoordinator = null;
                cancellation.Dispose();
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedCandidate)
            && _viewModel.SelectedCandidate is { } selected)
        {
            _trackCoordinator?.Prioritize(selected.TrackIdA, selected.TrackIdB);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.SimilarityDisplayLowerBoundPercent))
        {
            // MainWindowViewModelはPropertyChanged通知の直後に表示Filterを再構築するため、
            // 現在の同期コンテキストへ戻してから新しく表示されたCandidateのキャッシュを反映する。
            _ = RefreshVisibleAfterFilterAsync();
        }
    }

    private async Task RefreshVisibleAfterFilterAsync()
    {
        await Task.Yield();
        if (_disposed || _cancellation?.IsCancellationRequested == true)
        {
            return;
        }

        try
        {
            var database = new SqliteDatabase(_viewModel.DatabasePath);
            await database.InitializeAsync();
            await RefreshVisiblePresentationsAsync(
                new SqliteTrackQualityAnalysisRepository(database),
                new SqliteCandidateQualityComparisonRepository(database),
                CancellationToken.None);
        }
        catch (Exception)
        {
            // 表示Filter変更時のキャッシュ再表示は補助処理なので、バックグラウンド解析本体の状態を上書きしない。
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
