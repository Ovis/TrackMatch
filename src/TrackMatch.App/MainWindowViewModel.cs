using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TrackMatch.App.Playback;
using TrackMatch.App.Settings;
using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Libraries;
using TrackMatch.Core.Playback;
using TrackMatch.Core.Scanning;
using TrackMatch.Core.Trash;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Trash;

namespace TrackMatch.App;

/// <summary>
/// Library選択、表示Filter、分析進捗、候補レビュー画面の状態を管理する。
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly JsonAppSettingsStore _settingsStore = new();
    private readonly Dictionary<long, (long TrackIdA, long TrackIdB)> _sessionSelections = [];
    private readonly List<IncrementalScanError> _analysisErrors = [];
    private readonly List<CandidateReviewItemViewModel> _allCandidates = [];
    private readonly string _databasePath = TrackMatchDataPaths.DefaultDatabasePath;
    private CancellationTokenSource? _analysisCancellation;
    private TrackMatchAppSettings _settings = TrackMatchAppSettings.Default;
    private Library? _selectedLibrary;
    private CandidateReviewItemViewModel? _selectedCandidate;
    private CandidateReviewListMode _candidateListMode = CandidateReviewListMode.Unreviewed;
    private string _trashRoot = string.Empty;
    private string _statusText = "候補を読み込んでいます。";
    private string _analysisStatusText = "未実行";
    private string _trashStatusText = "未実行";
    private int _similarityDisplayLowerBoundPercent = 70;
    private bool _settingsLoaded;
    private bool _isLoading;
    private bool _isAnalyzing;
    private bool _isCancellingAnalysis;
    private bool _disposed;

    /// <summary>
    /// 候補レビュー画面のViewModelを生成する。
    /// </summary>
    /// <param name="playbackService">Candidate A/Bを同期再生するService</param>
    public MainWindowViewModel(ISynchronizedPlaybackService playbackService)
    {
        Playback = new SynchronizedPlaybackControlsViewModel(playbackService ?? throw new ArgumentNullException(nameof(playbackService)));
    }

    public ObservableCollection<Library> Libraries { get; } = [];
    public ObservableCollection<CandidateReviewItemViewModel> Candidates { get; } = [];
    public SynchronizedPlaybackControlsViewModel Playback { get; }
    public string DatabasePath => _databasePath;

    public Library? SelectedLibrary
    {
        get => _selectedLibrary;
        set
        {
            if (ReferenceEquals(_selectedLibrary, value) || _selectedLibrary?.Id == value?.Id) return;
            RememberCurrentCandidate();
            StopPlayback();
            _selectedLibrary = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLibrary));
            NotifyCommandStateChanged();
        }
    }

    public CandidateReviewItemViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (ReferenceEquals(_selectedCandidate, value)) return;
            _selectedCandidate = value;
            Playback.LoadCandidate(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanReview));
            OnPropertyChanged(nameof(CanClearReview));
            RememberCurrentCandidate();
        }
    }

    public CandidateReviewListMode CandidateListMode
    {
        get => _candidateListMode;
        set
        {
            if (_candidateListMode == value) return;
            _candidateListMode = value;
            OnPropertyChanged();
            ApplyCandidateFilter();
        }
    }

    public string TrashRoot
    {
        get => _trashRoot;
        private set
        {
            if (_trashRoot == value) return;
            _trashRoot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanProcessTrash));
        }
    }

    public int SimilarityDisplayLowerBoundPercent
    {
        get => _similarityDisplayLowerBoundPercent;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (_similarityDisplayLowerBoundPercent == normalized) return;
            _similarityDisplayLowerBoundPercent = normalized;
            OnPropertyChanged();
            ApplyCandidateFilter();
            _ = SaveSettingsSafeAsync();
        }
    }

    public int UnreviewedCount => _allCandidates.Count(item => !item.IsReviewed);
    public int ReviewedCount => _allCandidates.Count(item => item.IsReviewed);
    public int TotalCandidateCount => _allCandidates.Count;
    public bool HasLibrary => SelectedLibrary is not null;
    public bool HasSelection => SelectedCandidate is not null && !IsLoading;
    public bool CanReview => HasSelection && !IsAnalyzing;
    public bool CanClearReview => CanReview && SelectedCandidate?.IsReviewed == true;
    public bool CanAnalyzeLibrary => SelectedLibrary is not null && !IsLoading && !IsAnalyzing;
    public bool CanCancelAnalysis => IsAnalyzing && !IsCancellingAnalysis;
    public bool CanManageLibraries => !IsLoading && !IsAnalyzing;
    public bool CanProcessTrash => !IsLoading && !IsAnalyzing && SelectedLibrary is not null && File.Exists(DatabasePath);

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string AnalysisStatusText { get => _analysisStatusText; private set => SetField(ref _analysisStatusText, value); }
    public string TrashStatusText { get => _trashStatusText; private set => SetField(ref _trashStatusText, value); }

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetField(ref _isLoading, value)) NotifyCommandStateChanged(); }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set { if (SetField(ref _isAnalyzing, value)) NotifyCommandStateChanged(); }
    }

    public bool IsCancellingAnalysis
    {
        get => _isCancellingAnalysis;
        private set { if (SetField(ref _isCancellingAnalysis, value)) OnPropertyChanged(nameof(CanCancelAnalysis)); }
    }

    public int AnalysisErrorCount => _analysisErrors.Count;
    public IReadOnlyList<IncrementalScanError> AnalysisErrors => _analysisErrors;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// App設定、Library一覧、選択Libraryの候補を読み込む。
    /// </summary>
    public async Task LoadAsync(long? preferredLibraryId = null)
    {
        StopPlayback();
        IsLoading = true;
        try
        {
            if (!_settingsLoaded)
            {
                _settings = await _settingsStore.LoadAsync();
                _settingsLoaded = true;
                _similarityDisplayLowerBoundPercent = _settings.SimilarityDisplayLowerBoundPercent;
                TrashRoot = _settings.TrashRoot ?? string.Empty;
                OnPropertyChanged(nameof(SimilarityDisplayLowerBoundPercent));
            }

            var management = new LibraryManagementService(DatabasePath, TrashRoot);
            var libraries = await management.GetLibrariesAsync();
            Libraries.Clear();
            foreach (var library in libraries) Libraries.Add(library);
            var selectedId = preferredLibraryId ?? SelectedLibrary?.Id ?? _settings.LastSelectedLibraryId;
            SelectedLibrary = libraries.FirstOrDefault(item => item.Id == selectedId) ?? libraries.FirstOrDefault();
            await LoadCandidatesCoreAsync();
            await SaveSettingsSafeAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
        {
            _allCandidates.Clear();
            Candidates.Clear();
            SelectedCandidate = null;
            StatusText = $"読み込み失敗: {exception.Message}";
        }
        finally { IsLoading = false; }
    }

    public async Task SelectLibraryAsync(Library? library)
    {
        if (IsAnalyzing || IsLoading) return;
        SelectedLibrary = library;
        IsLoading = true;
        try { await LoadCandidatesCoreAsync(); await SaveSettingsSafeAsync(); }
        finally { IsLoading = false; }
    }

    public async Task SetTrashRootAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        TrashPathRules.ValidateRootSeparation(normalized, Libraries.SelectMany(library => library.Roots).Select(root => root.Path));
        TrashRoot = normalized;
        await SaveSettingsSafeAsync();
    }

    public async Task AnalyzeLibraryAsync()
    {
        var library = SelectedLibrary;
        if (library is null || !CanAnalyzeLibrary) { AnalysisStatusText = "Libraryを選択してください。"; return; }
        _analysisErrors.Clear();
        OnPropertyChanged(nameof(AnalysisErrorCount));
        IsAnalyzing = true;
        IsCancellingAnalysis = false;
        _analysisCancellation = new CancellationTokenSource();
        try
        {
            var fpcalcPath = Environment.GetEnvironmentVariable("TRACKMATCH_FPCALC") ?? "fpcalc";
            var workflow = new LibraryAnalysisWorkflow(DatabasePath, fpcalcPath);
            var progress = new Progress<LibraryAnalysisStage>(stage => AnalysisStatusText = stage switch
            {
                LibraryAnalysisStage.Scanning => "スキャン中...",
                LibraryAnalysisStage.GeneratingCandidates => "候補生成中...",
                LibraryAnalysisStage.AnalyzingCandidates => "詳細比較中...",
                _ => AnalysisStatusText,
            });
            var result = await workflow.RunAsync(library.Id, progress, _analysisCancellation.Token);
            var summaries = result.Scan.Roots.Select(item => item.Summary).ToArray();
            foreach (var error in result.Scan.Roots.SelectMany(item => item.Errors)) _analysisErrors.Add(error);
            AnalysisStatusText = FormatAnalysisSummary("完了", summaries);
        }
        catch (OperationCanceledException) { AnalysisStatusText = "キャンセルしました — 完了済みの処理は保持されています。"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        { AnalysisStatusText = $"分析失敗: {exception.Message}"; }
        finally
        {
            _analysisCancellation.Dispose();
            _analysisCancellation = null;
            IsCancellingAnalysis = false;
            IsAnalyzing = false;
            OnPropertyChanged(nameof(AnalysisErrorCount));
        }
        IsLoading = true;
        try { await LoadCandidatesCoreAsync(); }
        finally { IsLoading = false; }
    }

    public void CancelAnalysis()
    {
        if (!CanCancelAnalysis || _analysisCancellation is null) return;
        IsCancellingAnalysis = true;
        AnalysisStatusText = "キャンセル中...";
        _analysisCancellation.Cancel();
    }

    public void StopPlayback() => Playback.Stop();
    public Task MarkNotDuplicateAsync() => SaveReviewAsync(CandidateReviewDecision.NotDuplicate, null);
    public Task ConfirmDuplicateKeepAAsync() => SaveReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdA);
    public Task ConfirmDuplicateKeepBAsync() => SaveReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdB);

    /// <summary>
    /// 現在のレビューを削除し、候補を未レビューへ戻す。
    /// </summary>
    public async Task ClearReviewAsync()
    {
        var selected = SelectedCandidate;
        if (selected is null || !CanClearReview) return;
        StopPlayback();
        IsLoading = true;
        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            await new SqliteCandidateReviewRepository(database).DeleteAsync(CandidatePairKey.Create(selected.TrackIdA, selected.TrackIdB));
            await ReloadCandidatesPreservingPairAsync(selected.TrackIdA, selected.TrackIdB);
        }
        finally { IsLoading = false; }
    }

    public async Task<RejectedTrackTrashResult?> ProcessTrashAsync(bool execute, TrashDestinationCollisionBehavior collisionBehavior = TrashDestinationCollisionBehavior.Skip)
    {
        var library = SelectedLibrary;
        if (!CanProcessTrash || library is null) { TrashStatusText = "Libraryを選択してください。"; return null; }
        if (string.IsNullOrWhiteSpace(TrashRoot)) { TrashStatusText = "Trash Rootを設定してください。"; return null; }
        StopPlayback();
        IsLoading = true;
        try
        {
            TrashPathRules.ValidateRootSeparation(TrashRoot, Libraries.SelectMany(item => item.Roots).Select(root => root.Path));
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            var tracks = new SqliteTrackRepository(database);
            var service = new RejectedTrackTrashService(new SqliteCandidateReviewRepository(database), new SqliteTrackLookupRepository(database), tracks, new LocalTrackFileOperations());
            var result = await service.ProcessAsync(library.Id, TrashRoot, execute, collisionBehavior);
            TrashStatusText = execute ? $"移動完了 {result.MovedCount}件 / Blocked {result.BlockedCount}件" : $"確認: 移動可能 {result.ReadyCount}件 / Blocked {result.BlockedCount}件";
            return result;
        }
        finally { IsLoading = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        Playback.Dispose();
    }

    private async Task LoadCandidatesCoreAsync()
    {
        _allCandidates.Clear();
        Candidates.Clear();
        SelectedCandidate = null;
        var library = SelectedLibrary;
        if (library is null) { StatusText = "Libraryがありません。管理... から作成してください。"; return; }
        var database = new SqliteDatabase(DatabasePath);
        await database.InitializeAsync();
        var rows = await new SqliteCandidateReviewReportRepository(database).GetAsync(library.Id);
        _allCandidates.AddRange(rows.Select(row => new CandidateReviewItemViewModel(row)));
        NotifyCandidateCountsChanged();
        ApplyCandidateFilter();
    }

    private void ApplyCandidateFilter()
    {
        var library = SelectedLibrary;
        var previous = SelectedCandidate;
        var minimum = SimilarityDisplayLowerBoundPercent / 100d;
        var visible = _allCandidates.Where(item => item.Row.Similarity >= minimum).Where(item => CandidateListMode switch
        {
            CandidateReviewListMode.Unreviewed => !item.IsReviewed,
            CandidateReviewListMode.Reviewed => item.IsReviewed,
            _ => true,
        }).ToArray();

        if (previous is not null && !visible.Any(item => SameCandidate(item, previous))) StopPlayback();
        Candidates.Clear();
        foreach (var item in visible) Candidates.Add(item);
        CandidateReviewItemViewModel? selection = null;
        if (library is not null && _sessionSelections.TryGetValue(library.Id, out var remembered))
            selection = Candidates.FirstOrDefault(item => item.TrackIdA == remembered.TrackIdA && item.TrackIdB == remembered.TrackIdB);
        if (selection is null && previous is not null) selection = Candidates.FirstOrDefault(item => SameCandidate(item, previous));
        SelectedCandidate = selection ?? Candidates.FirstOrDefault();
        if (library is not null)
            StatusText = $"{library.Name} — 表示 {Candidates.Count} / 未レビュー {UnreviewedCount}件";
    }

    private async Task SaveReviewAsync(CandidateReviewDecision decision, long? keepTrackId)
    {
        var selected = SelectedCandidate;
        if (selected is null || !CanReview) return;
        StopPlayback();
        IsLoading = true;
        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            await new SqliteCandidateReviewRepository(database).SaveAsync(new CandidateReview(CandidatePairKey.Create(selected.TrackIdA, selected.TrackIdB), decision, null, keepTrackId));
            await ReloadCandidatesPreservingPairAsync(selected.TrackIdA, selected.TrackIdB);
        }
        finally { IsLoading = false; }
    }

    private async Task ReloadCandidatesPreservingPairAsync(long trackIdA, long trackIdB)
    {
        var library = SelectedLibrary;
        if (library is null) return;
        var database = new SqliteDatabase(DatabasePath);
        await database.InitializeAsync();
        var rows = await new SqliteCandidateReviewReportRepository(database).GetAsync(library.Id);
        _allCandidates.Clear();
        _allCandidates.AddRange(rows.Select(row => new CandidateReviewItemViewModel(row)));
        NotifyCandidateCountsChanged();
        ApplyCandidateFilter();
        SelectedCandidate = Candidates.FirstOrDefault(item => item.TrackIdA == trackIdA && item.TrackIdB == trackIdB) ?? SelectedCandidate;
    }

    private void NotifyCandidateCountsChanged()
    {
        OnPropertyChanged(nameof(UnreviewedCount));
        OnPropertyChanged(nameof(ReviewedCount));
        OnPropertyChanged(nameof(TotalCandidateCount));
    }

    private async Task SaveSettingsSafeAsync()
    {
        if (!_settingsLoaded) return;
        _settings = new TrackMatchAppSettings(SelectedLibrary?.Id, SimilarityDisplayLowerBoundPercent, string.IsNullOrWhiteSpace(TrashRoot) ? null : TrashRoot);
        try { await _settingsStore.SaveAsync(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { StatusText = $"設定保存失敗: {exception.Message}"; }
    }

    private static bool SameCandidate(CandidateReviewItemViewModel left, CandidateReviewItemViewModel right)
        => left.TrackIdA == right.TrackIdA && left.TrackIdB == right.TrackIdB;

    private string FormatAnalysisSummary(string prefix, IReadOnlyCollection<ScanSessionSummary> summaries)
    {
        var total = summaries.Sum(item => item.TotalFiles);
        var added = summaries.Sum(item => item.AddedFiles);
        var updated = summaries.Sum(item => item.UpdatedFiles);
        var missing = summaries.Sum(item => item.RemovedFiles);
        var errors = summaries.Sum(item => item.ErrorCount);
        return $"{prefix}{(errors > 0 ? "（エラーあり）" : string.Empty)} — 対象 {total:N0}曲 / 新規 {added:N0} / 更新 {updated:N0} / Missing {missing:N0} / エラー {errors:N0}";
    }

    private void RememberCurrentCandidate()
    {
        if (_selectedLibrary is not null && _selectedCandidate is not null)
            _sessionSelections[_selectedLibrary.Id] = (_selectedCandidate.TrackIdA, _selectedCandidate.TrackIdB);
    }

    private void NotifyCommandStateChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanClearReview));
        OnPropertyChanged(nameof(CanAnalyzeLibrary));
        OnPropertyChanged(nameof(CanCancelAnalysis));
        OnPropertyChanged(nameof(CanManageLibraries));
        OnPropertyChanged(nameof(CanProcessTrash));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 候補一覧で表示するレビュー状態を表す。
/// </summary>
public enum CandidateReviewListMode
{
    Unreviewed,
    Reviewed,
    All,
}
