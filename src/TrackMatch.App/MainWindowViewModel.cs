using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using TrackMatch.App.Playback;
using TrackMatch.App.Settings;
using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Libraries;
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
    private readonly ITrackPlaybackService _playbackService;
    private readonly JsonAppSettingsStore _settingsStore = new();
    private readonly Dictionary<long, (long TrackIdA, long TrackIdB)> _sessionSelections = [];
    private readonly List<IncrementalScanError> _analysisErrors = [];
    private readonly List<CandidateReviewItemViewModel> _allCandidates = [];
    private readonly string _databasePath = TrackMatchDataPaths.DefaultDatabasePath;
    private CancellationTokenSource? _analysisCancellation;
    private TrackMatchAppSettings _settings = TrackMatchAppSettings.Default;
    private Library? _selectedLibrary;
    private CandidateReviewItemViewModel? _selectedCandidate;
    private string _trashRoot = string.Empty;
    private string _statusText = "候補を読み込んでいます。";
    private string _analysisStatusText = "ライブラリ分析は未実行";
    private string _playbackStatusText = "停止中";
    private string _trashStatusText = "Trash処理は未実行";
    private int _similarityDisplayLowerBoundPercent = 70;
    private bool _settingsLoaded;
    private bool _isLoading;
    private bool _isAnalyzing;
    private bool _isCancellingAnalysis;
    private bool _disposed;

    /// <summary>
    /// 候補レビュー画面のViewModelを生成する。
    /// </summary>
    public MainWindowViewModel(ITrackPlaybackService playbackService)
    {
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _playbackService.PlaybackEnded += OnPlaybackEnded;
        _playbackService.PlaybackFailed += OnPlaybackFailed;
    }

    public ObservableCollection<Library> Libraries { get; } = [];

    /// <summary>
    /// Similarity表示下限を満たすCandidateだけを保持する表示用Collection。
    /// </summary>
    public ObservableCollection<CandidateReviewItemViewModel> Candidates { get; } = [];

    public string DatabasePath => _databasePath;

    public Library? SelectedLibrary
    {
        get => _selectedLibrary;
        set
        {
            if (ReferenceEquals(_selectedLibrary, value) || _selectedLibrary?.Id == value?.Id)
            {
                return;
            }

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
            if (ReferenceEquals(_selectedCandidate, value))
            {
                return;
            }

            StopPlayback();
            _selectedCandidate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanReview));
            RememberCurrentCandidate();
        }
    }

    public string TrashRoot
    {
        get => _trashRoot;
        private set
        {
            if (_trashRoot == value)
            {
                return;
            }

            _trashRoot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanProcessTrash));
        }
    }

    /// <summary>
    /// Candidate一覧へ表示するSimilarityの下限を百分率で取得・設定する。
    /// </summary>
    public int SimilarityDisplayLowerBoundPercent
    {
        get => _similarityDisplayLowerBoundPercent;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (_similarityDisplayLowerBoundPercent == normalized)
            {
                return;
            }

            _similarityDisplayLowerBoundPercent = normalized;
            OnPropertyChanged();
            ApplyCandidateFilter();
            _ = SaveSettingsSafeAsync();
        }
    }

    public bool HasLibrary => SelectedLibrary is not null;

    public bool HasSelection => SelectedCandidate is not null && !IsLoading;

    public bool CanReview => HasSelection && !IsAnalyzing;

    public bool CanAnalyzeLibrary => SelectedLibrary is not null && !IsLoading && !IsAnalyzing;

    public bool CanCancelAnalysis => IsAnalyzing && !IsCancellingAnalysis;

    public bool CanManageLibraries => !IsLoading && !IsAnalyzing;

    // TrashのLibrary-wide再設計は後続PRで行う。移行中は単一Root Libraryだけ従来処理を許可する。
    public bool CanProcessTrash => !IsLoading
        && !IsAnalyzing
        && SelectedLibrary?.Roots.Count == 1
        && !string.IsNullOrWhiteSpace(TrashRoot)
        && File.Exists(DatabasePath);

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string AnalysisStatusText
    {
        get => _analysisStatusText;
        private set => SetField(ref _analysisStatusText, value);
    }

    public string PlaybackStatusText
    {
        get => _playbackStatusText;
        private set => SetField(ref _playbackStatusText, value);
    }

    public string TrashStatusText
    {
        get => _trashStatusText;
        private set => SetField(ref _trashStatusText, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetField(ref _isAnalyzing, value))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public bool IsCancellingAnalysis
    {
        get => _isCancellingAnalysis;
        private set
        {
            if (SetField(ref _isCancellingAnalysis, value))
            {
                OnPropertyChanged(nameof(CanCancelAnalysis));
            }
        }
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

            var management = new LibraryManagementService(DatabasePath);
            var libraries = await management.GetLibrariesAsync();
            Libraries.Clear();
            foreach (var library in libraries)
            {
                Libraries.Add(library);
            }

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
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Main WindowでLibraryを切り替え、Library scopeの候補だけを読み込む。
    /// </summary>
    public async Task SelectLibraryAsync(Library? library)
    {
        if (IsAnalyzing || IsLoading)
        {
            return;
        }

        SelectedLibrary = library;
        IsLoading = true;
        try
        {
            await LoadCandidatesCoreAsync();
            await SaveSettingsSafeAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Trash RootをApp-wide設定として更新する。
    /// </summary>
    public async Task SetTrashRootAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        TrashRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        await SaveSettingsSafeAsync();
    }

    /// <summary>
    /// 選択Libraryの全Rootを増分走査し、Library内候補生成と詳細比較まで実行する。
    /// </summary>
    public async Task AnalyzeLibraryAsync()
    {
        var library = SelectedLibrary;
        if (library is null || !CanAnalyzeLibrary)
        {
            AnalysisStatusText = "Libraryを選択してください。";
            return;
        }

        _analysisErrors.Clear();
        OnPropertyChanged(nameof(AnalysisErrorCount));
        IsAnalyzing = true;
        IsCancellingAnalysis = false;
        _analysisCancellation = new CancellationTokenSource();
        try
        {
            var fpcalcPath = Environment.GetEnvironmentVariable("TRACKMATCH_FPCALC") ?? "fpcalc";
            var workflow = new LibraryAnalysisWorkflow(DatabasePath, fpcalcPath);
            var progress = new Progress<LibraryAnalysisStage>(stage =>
            {
                AnalysisStatusText = stage switch
                {
                    LibraryAnalysisStage.Scanning => "スキャン中...",
                    LibraryAnalysisStage.GeneratingCandidates => "候補生成中...",
                    LibraryAnalysisStage.AnalyzingCandidates => "詳細比較中...",
                    _ => AnalysisStatusText,
                };
            });

            var result = await workflow.RunAsync(library.Id, progress, _analysisCancellation.Token);
            var summaries = result.Scan.Roots.Select(item => item.Summary).ToArray();
            foreach (var error in result.Scan.Roots.SelectMany(item => item.Errors))
            {
                _analysisErrors.Add(error);
            }

            AnalysisStatusText = FormatAnalysisSummary("完了", summaries);
        }
        catch (OperationCanceledException)
        {
            AnalysisStatusText = "キャンセルしました — 完了済みの処理は保持されています。次回は増分で続行できます。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            AnalysisStatusText = $"分析失敗: {exception.Message}";
        }
        finally
        {
            _analysisCancellation.Dispose();
            _analysisCancellation = null;
            IsCancellingAnalysis = false;
            IsAnalyzing = false;
            OnPropertyChanged(nameof(AnalysisErrorCount));
        }

        // Scan中は開始時Snapshotを維持し、完了・Cancel・失敗後に初めてDBから一覧を入れ替える。
        IsLoading = true;
        try
        {
            await LoadCandidatesCoreAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 実行中分析へCancellationを要求する。
    /// </summary>
    public void CancelAnalysis()
    {
        if (!CanCancelAnalysis || _analysisCancellation is null)
        {
            return;
        }

        IsCancellingAnalysis = true;
        AnalysisStatusText = "キャンセル中...";
        _analysisCancellation.Cancel();
    }

    public void PlayTrackA() => PlaySelectedTrack(isTrackA: true);

    public void PlayTrackB() => PlaySelectedTrack(isTrackA: false);

    public void StopPlayback()
    {
        _playbackService.Stop();
        PlaybackStatusText = "停止中";
    }

    public Task MarkNotDuplicateAsync() => SaveReviewAsync(CandidateReviewDecision.NotDuplicate, null);

    public Task ConfirmDuplicateKeepAAsync() => SaveReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdA);

    public Task ConfirmDuplicateKeepBAsync() => SaveReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdB);

    public async Task<RejectedTrackTrashResult?> ProcessTrashAsync(bool execute)
    {
        var root = SelectedLibrary?.Roots.SingleOrDefault();
        if (!CanProcessTrash || root is null)
        {
            TrashStatusText = "Trash処理は現在、Rootが1つのLibraryでのみ利用できます。";
            return null;
        }

        StopPlayback();
        IsLoading = true;
        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            var tracks = new SqliteTrackRepository(database);
            var service = new RejectedTrackTrashService(
                new SqliteCandidateReviewRepository(database),
                new SqliteTrackLookupRepository(database),
                tracks,
                new LocalTrackFileOperations());
            var result = await service.ProcessAsync(root.Path, TrashRoot, execute);
            TrashStatusText = execute
                ? $"Trash移動完了: {result.MovedCount}件 / Blocked: {result.BlockedCount}件"
                : $"Dry-run: 移動可能 {result.ReadyCount}件 / Blocked: {result.BlockedCount}件";
            return result;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _playbackService.PlaybackEnded -= OnPlaybackEnded;
        _playbackService.PlaybackFailed -= OnPlaybackFailed;
        _playbackService.Dispose();
    }

    private async Task LoadCandidatesCoreAsync()
    {
        _allCandidates.Clear();
        Candidates.Clear();
        SelectedCandidate = null;
        var library = SelectedLibrary;
        if (library is null)
        {
            StatusText = "Libraryがありません。管理... から作成してください。";
            return;
        }

        var database = new SqliteDatabase(DatabasePath);
        await database.InitializeAsync();
        var rows = await new SqliteCandidateReviewReportRepository(database).GetAsync(library.Id);
        _allCandidates.AddRange(rows.Select(row => new CandidateReviewItemViewModel(row)));
        ApplyCandidateFilter();
    }

    /// <summary>
    /// 現在のSimilarity下限で表示Collectionだけを再構築する。
    /// </summary>
    private void ApplyCandidateFilter()
    {
        var library = SelectedLibrary;
        var previous = SelectedCandidate;
        var minimum = SimilarityDisplayLowerBoundPercent / 100d;
        var visible = _allCandidates
            .Where(item => item.Row.Similarity >= minimum)
            .ToArray();

        // 選択中CandidateがFilter外になる場合、旧Audioを流し続けないことを先に保証する。
        if (previous is not null && !visible.Any(item => SameCandidate(item, previous)))
        {
            StopPlayback();
        }

        Candidates.Clear();
        foreach (var item in visible)
        {
            Candidates.Add(item);
        }

        CandidateReviewItemViewModel? selection = null;
        if (library is not null && _sessionSelections.TryGetValue(library.Id, out var remembered))
        {
            selection = Candidates.FirstOrDefault(item =>
                item.TrackIdA == remembered.TrackIdA && item.TrackIdB == remembered.TrackIdB);
        }

        if (selection is null && previous is not null)
        {
            selection = Candidates.FirstOrDefault(item => SameCandidate(item, previous));
        }

        SelectedCandidate = selection ?? Candidates.FirstOrDefault();
        if (library is not null)
        {
            StatusText = $"{library.Name} — 表示 {Candidates.Count} / 未レビュー {_allCandidates.Count}件（Similarity {SimilarityDisplayLowerBoundPercent}%以上）";
        }
    }

    private void PlaySelectedTrack(bool isTrackA)
    {
        var selected = SelectedCandidate;
        if (selected is null || IsLoading)
        {
            return;
        }

        var path = isTrackA ? selected.Row.PathA : selected.Row.PathB;
        var title = isTrackA ? selected.TitleA : selected.TitleB;
        var side = isTrackA ? "A" : "B";
        try
        {
            _playbackService.Play(path);
            PlaybackStatusText = $"再生中: {side} / {title}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            PlaybackStatusText = $"再生失敗: {exception.Message}";
        }
    }

    private async Task SaveReviewAsync(CandidateReviewDecision decision, long? keepTrackId)
    {
        var selected = SelectedCandidate;
        if (selected is null || !CanReview)
        {
            return;
        }

        StopPlayback();
        IsLoading = true;
        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            await new SqliteCandidateReviewRepository(database).SaveAsync(new CandidateReview(
                CandidatePairKey.Create(selected.TrackIdA, selected.TrackIdB), decision, null, keepTrackId));

            var visibleIndex = Candidates.IndexOf(selected);
            _allCandidates.RemoveAll(item => SameCandidate(item, selected));
            Candidates.Remove(selected);
            SelectedCandidate = Candidates.Count == 0 ? null : Candidates[Math.Min(visibleIndex, Candidates.Count - 1)];
            StatusText = SelectedLibrary is null
                ? $"レビューを保存しました。残り {Candidates.Count}件"
                : $"{SelectedLibrary.Name} — 表示 {Candidates.Count} / 未レビュー {_allCandidates.Count}件（Similarity {SimilarityDisplayLowerBoundPercent}%以上）";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SaveSettingsSafeAsync()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        _settings = new TrackMatchAppSettings(
            SelectedLibrary?.Id,
            SimilarityDisplayLowerBoundPercent,
            string.IsNullOrWhiteSpace(TrashRoot) ? null : TrashRoot);
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusText = $"設定保存失敗: {exception.Message}";
        }
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
        {
            _sessionSelections[_selectedLibrary.Id] = (_selectedCandidate.TrackIdA, _selectedCandidate.TrackIdB);
        }
    }

    private void NotifyCommandStateChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanAnalyzeLibrary));
        OnPropertyChanged(nameof(CanCancelAnalysis));
        OnPropertyChanged(nameof(CanManageLibraries));
        OnPropertyChanged(nameof(CanProcessTrash));
    }

    private void OnPlaybackEnded(object? sender, EventArgs e) => PlaybackStatusText = "再生終了";

    private void OnPlaybackFailed(string message) => PlaybackStatusText = $"再生失敗: {message}";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
