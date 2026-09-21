using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using TrackMatch.App.Playback;
using TrackMatch.App.Settings;
using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Libraries;
using TrackMatch.Core.Scanning;
using TrackMatch.Core.Trash;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Trash;

namespace TrackMatch.App;

/// <summary>
/// Library選択、表示Filter、分析進捗、候補レビュー画面の状態を管理する。
/// </summary>
public sealed partial class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly JsonAppSettingsStore _settingsStore = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Dictionary<long, (long TrackIdA, long TrackIdB)> _sessionSelections = [];
    private readonly List<IncrementalScanError> _analysisErrors = [];
    private readonly List<ContentChangeNotice> _contentChanges = [];
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

    /// <summary>候補レビュー画面のViewModelを生成する。</summary>
    /// <param name="playbackService">Candidate A/Bを同期再生するService</param>
    /// <param name="loggerFactory">Application層を含む診断Loggerを生成するFactory</param>
    public MainWindowViewModel(ISynchronizedPlaybackService playbackService, ILoggerFactory? loggerFactory = null)
    {
        Playback = new SynchronizedPlaybackControlsViewModel(playbackService ?? throw new ArgumentNullException(nameof(playbackService)));
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<MainWindowViewModel>();
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

            _selectedCandidate = value;
            Playback.LoadCandidate(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanReview));
            OnPropertyChanged(nameof(CanClearReview));
            RememberCurrentCandidate();
            _ = LoadDuplicateGroupsForSelectionAsync(value);
        }
    }

    public CandidateReviewListMode CandidateListMode
    {
        get => _candidateListMode;
        set { if (_candidateListMode == value) { return; } _candidateListMode = value; OnPropertyChanged(); ApplyCandidateFilter(); }
    }

    public string TrashRoot
    {
        get => _trashRoot;
        private set { if (_trashRoot == value) { return; } _trashRoot = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanProcessTrash)); }
    }

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
            NotifyCandidateCountsChanged();
            ApplyCandidateFilter();
            _ = SaveSettingsSafeAsync();
        }
    }

    // タブ件数はDB全体ではなく、現在レビュー対象としている一致度下限を反映する。
    // レビュー省略はHuman Verdictではないためレビュー済みに数えず、操作不要なので未レビューにも数えない。
    public int UnreviewedCount => ReviewTargetCandidates.Count(item => !item.IsReviewed && !item.IsReviewSkipped);
    public int ReviewedCount => ReviewTargetCandidates.Count(item => item.IsReviewed && !item.IsHumanVerdictSuspended);
    public int SuspendedReviewCount => ReviewTargetCandidates.Count(item => item.IsHumanVerdictSuspended);
    public string ReviewedTabHeader => SuspendedReviewCount == 0
        ? $"レビュー済み ({ReviewedCount})"
        : $"レビュー済み {ReviewedCount}（利用停止 {SuspendedReviewCount}）";
    public int ReReviewRecommendedCount => ReviewTargetCandidates.Count(item => item.IsReviewed && !item.IsHumanVerdictSuspended && item.IsReReviewRecommended);
    public int TotalCandidateCount => ReviewTargetCandidates.Count();
    public bool HasLibrary => SelectedLibrary is not null;
    public bool HasSelection => SelectedCandidate is not null && !IsLoading;
    // 省略Candidateもユーザーが直接レビューする場合は通常の3択を使える。
    public bool CanReview => HasSelection && !IsAnalyzing && SelectedCandidate?.IsHumanVerdictSuspended != true;
    public bool CanClearReview => HasSelection && !IsAnalyzing && SelectedCandidate?.IsReviewed == true;
    public bool CanAnalyzeLibrary => SelectedLibrary is not null && !IsLoading && !IsAnalyzing;
    public bool CanCancelAnalysis => IsAnalyzing && !IsCancellingAnalysis;
    public bool CanManageLibraries => !IsLoading && !IsAnalyzing;
    public bool CanProcessTrash => !IsLoading && !IsAnalyzing && SelectedLibrary is not null && File.Exists(DatabasePath);
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string AnalysisStatusText { get => _analysisStatusText; private set => SetField(ref _analysisStatusText, value); }
    public string TrashStatusText { get => _trashStatusText; private set => SetField(ref _trashStatusText, value); }
    public bool IsLoading { get => _isLoading; private set { if (SetField(ref _isLoading, value)) { NotifyCommandStateChanged(); } } }
    public bool IsAnalyzing { get => _isAnalyzing; private set { if (SetField(ref _isAnalyzing, value)) { NotifyCommandStateChanged(); } } }
    public bool IsCancellingAnalysis { get => _isCancellingAnalysis; private set { if (SetField(ref _isCancellingAnalysis, value)) { OnPropertyChanged(nameof(CanCancelAnalysis)); } } }
    public int AnalysisErrorCount => _analysisErrors.Count;
    public IReadOnlyList<IncrementalScanError> AnalysisErrors => _analysisErrors;
    public int ContentChangeCount => _contentChanges.Count;
    public IReadOnlyList<ContentChangeNotice> ContentChanges => _contentChanges;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>App設定、Library一覧、選択Libraryの候補を読み込む。</summary>
    public async Task LoadAsync(long? preferredLibraryId = null)
    {
        StopPlayback(); IsLoading = true;
        try
        {
            if (!_settingsLoaded)
            {
                _settings = await _settingsStore.LoadAsync(); _settingsLoaded = true;
                _similarityDisplayLowerBoundPercent = _settings.SimilarityDisplayLowerBoundPercent;
                TrashRoot = _settings.TrashRoot ?? string.Empty;
                OnPropertyChanged(nameof(SimilarityDisplayLowerBoundPercent));
            }
            var management = new LibraryManagementService(DatabasePath, TrashRoot);
            // Library取得にはDB初期化や派生状態の同期が含まれるため、起動時にDispatcherを占有させない。
            // 取得結果をObservableCollectionへ反映する処理だけをUI Threadへ戻して実行する。
            var libraries = await Task.Run(() => management.GetLibrariesAsync());
            Libraries.Clear(); foreach (var library in libraries)
            {
                Libraries.Add(library);
            }

            var selectedId = preferredLibraryId ?? SelectedLibrary?.Id ?? _settings.LastSelectedLibraryId;
            SelectedLibrary = libraries.FirstOrDefault(item => item.Id == selectedId) ?? libraries.FirstOrDefault();
            await LoadCandidatesCoreAsync(); await SaveSettingsSafeAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
        {
            _allCandidates.Clear(); Candidates.Clear(); SelectedCandidate = null; StatusText = $"読み込み失敗: {exception.Message}";
        }
        finally { IsLoading = false; }
    }

    public async Task SelectLibraryAsync(Library? library)
    {
        if (IsAnalyzing || IsLoading)
        {
            return;
        }

        SelectedLibrary = library; IsLoading = true;
        try { await LoadCandidatesCoreAsync(); await SaveSettingsSafeAsync(); }
        finally { IsLoading = false; }
    }

    public async Task SetTrashRootAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        TrashPathRules.ValidateRootSeparation(normalized, Libraries.SelectMany(library => library.Roots).Select(root => root.Path));
        TrashRoot = normalized; await SaveSettingsSafeAsync();
    }

    public async Task AnalyzeLibraryAsync()
    {
        var library = SelectedLibrary;
        if (library is null || !CanAnalyzeLibrary) { AnalysisStatusText = "ライブラリを選択してください。"; return; }
        _analysisErrors.Clear(); _contentChanges.Clear();
        OnPropertyChanged(nameof(AnalysisErrorCount)); OnPropertyChanged(nameof(ContentChangeCount)); IsAnalyzing = true; IsCancellingAnalysis = false;
        _analysisCancellation = new CancellationTokenSource();
        try
        {
            var fpcalcPath = Environment.GetEnvironmentVariable("TRACKMATCH_FPCALC") ?? "fpcalc";
            var workflow = new LibraryAnalysisWorkflow(DatabasePath, fpcalcPath, logger: _loggerFactory.CreateLogger<LibraryAnalysisWorkflow>());
            var progress = new Progress<LibraryAnalysisProgress>(value => AnalysisStatusText = FormatAnalysisProgress(value));
            _logger.LogInformation("ライブラリ分析を開始する LibraryId={LibraryId} ThreadId={ThreadId}", library.Id, Environment.CurrentManagedThreadId);
            // Workflow内部には同期ファイル列挙やCPU処理が含まれる。asyncメソッドをUI Threadから直接呼ぶだけでは
            // それらもDispatcher Thread上で実行されるため、Workflow全体を明示的にThreadPoolへ移す。
            // Progress<T>はUI Thread上で生成済みなので、画面更新だけはDispatcherへ戻る。
            var result = await Task.Run(
                () => workflow.RunAsync(library.Id, progress, _analysisCancellation.Token),
                _analysisCancellation.Token);
            _logger.LogInformation("ライブラリ分析が完了した LibraryId={LibraryId} ThreadId={ThreadId}", library.Id, Environment.CurrentManagedThreadId);
            var summaries = result.Scan.Roots.Select(item => item.Summary).ToArray();
            foreach (var error in result.Scan.Roots.SelectMany(item => item.Errors))
            {
                _analysisErrors.Add(error);
            }

            AnalysisStatusText = FormatAnalysisSummary("完了", summaries);
            _contentChanges.AddRange(result.Scan.Roots.SelectMany(item => item.ContentChanges));
            OnPropertyChanged(nameof(ContentChangeCount));
            if (_contentChanges.Count != 0)
            {
                var invalidatedReviews = _contentChanges.Sum(item => item.InvalidatedReviewCount);
                AnalysisStatusText += _contentChanges.Count == 1
                    ? $" — 音声内容変更: {Path.GetFileName(_contentChanges[0].Path)} / レビュー判定解除 {invalidatedReviews}件"
                    : $" — 音声内容変更 {_contentChanges.Count}ファイル / レビュー判定解除 {invalidatedReviews}件";
            }
        }
        catch (OperationCanceledException) { _logger.LogInformation("ライブラリ分析がキャンセルされた LibraryId={LibraryId}", library.Id); AnalysisStatusText = "キャンセルしました — 完了済みの処理は保持されています。"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException) { _logger.LogError(exception, "ライブラリ分析に失敗した LibraryId={LibraryId}", library.Id); AnalysisStatusText = $"分析失敗: {exception.Message}"; }
        finally
        {
            _analysisCancellation.Dispose(); _analysisCancellation = null; IsCancellingAnalysis = false; IsAnalyzing = false; OnPropertyChanged(nameof(AnalysisErrorCount));
        }
        IsLoading = true; try { await LoadCandidatesCoreAsync(); } finally { IsLoading = false; }
    }

    public void CancelAnalysis()
    {
        if (!CanCancelAnalysis || _analysisCancellation is null)
        {
            return;
        }

        IsCancellingAnalysis = true; AnalysisStatusText = "キャンセル中..."; _analysisCancellation.Cancel();
    }

    public void StopPlayback() => Playback.Stop();
    public Task MarkNotDuplicateAsync() => SaveReviewAsync(CandidateReviewDecision.NotDuplicate, null);
    public Task ConfirmDuplicateKeepAAsync() => SaveReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdA);
    public Task ConfirmDuplicateKeepBAsync() => SaveReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdB);

    /// <summary>現在のHuman Verdictを解除し、残った判定から候補状態を再計算する。</summary>
    public async Task ClearReviewAsync()
    {
        var selected = SelectedCandidate; if (selected is null || !CanClearReview)
        {
            return;
        }

        var library = SelectedLibrary; if (library is null)
        {
            return;
        }

        StopPlayback(); IsLoading = true;
        try
        {
            var pair = CandidatePairKey.Create(selected.TrackIdA, selected.TrackIdB);
            // Verdict更新後はGlobal Group再同期や補完Candidate生成まで連鎖するため、
            // DB・CPU処理をUI Threadから分離し、表示状態の差し替えだけをawait後に行う。
            await Task.Run(() => DeleteReviewAndRefreshDerivedStateAsync(DatabasePath, library.Id, pair));
            await ReloadCandidatesPreservingPairAsync(selected.TrackIdA, selected.TrackIdB);
        }
        finally { IsLoading = false; }
    }

    public async Task<RejectedTrackTrashResult?> ProcessTrashAsync(bool execute, TrashDestinationCollisionBehavior collisionBehavior = TrashDestinationCollisionBehavior.Skip)
    {
        var library = SelectedLibrary;
        if (!CanProcessTrash || library is null) { TrashStatusText = "ライブラリを選択してください。"; return null; }
        if (string.IsNullOrWhiteSpace(TrashRoot)) { TrashStatusText = "ごみ箱フォルダを設定してください。"; return null; }
        StopPlayback(); IsLoading = true;
        try
        {
            var trashRoot = TrashRoot;
            var libraryRoots = Libraries.SelectMany(item => item.Roots).Select(root => root.Path).ToArray();
            TrashPathRules.ValidateRootSeparation(trashRoot, libraryRoots);
            // TrashはNASを含む実ファイルI/OとGlobal Group再同期を行うため、UI Threadでは実行しない。
            var result = await Task.Run(() => ProcessTrashCoreAsync(
                DatabasePath,
                library.Id,
                trashRoot,
                execute,
                collisionBehavior));

            if (execute && result.MovedCount > 0)
            {
                // UI公開中のCandidate集合は、バックグラウンド側の派生状態更新が完了してから差し替える。
                await LoadCandidatesCoreAsync();
            }

            TrashStatusText = execute ? $"移動完了 {result.MovedCount}件 / 移動不可 {result.BlockedCount}件" : $"確認: 移動可能 {result.ReadyCount}件 / 移動不可 {result.BlockedCount}件";
            return result;
        }
        finally { IsLoading = false; }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true; _analysisCancellation?.Cancel(); _analysisCancellation?.Dispose(); Playback.Dispose();
    }

    private IEnumerable<CandidateReviewItemViewModel> ReviewTargetCandidates
    {
        get
        {
            var minimum = SimilarityDisplayLowerBoundPercent / 100d;
            return _allCandidates.Where(item => item.Row.IsSupplementalCandidate || item.Row.Similarity >= minimum);
        }
    }

    private async Task LoadCandidatesCoreAsync()
    {
        _allCandidates.Clear(); Candidates.Clear(); SelectedCandidate = null;
        var library = SelectedLibrary;
        if (library is null) { StatusText = "ライブラリがありません。［管理...］から作成してください。"; return; }
        // DB同期、Supplemental Candidate生成、Presentation State構築は候補数に応じて重くなるため、
        // UI Threadでは実行しない。ObservableCollectionなどWPFへ公開する状態の更新だけをawait後に行う。
        var loadedCandidates = await Task.Run(() => LoadCandidatesForLibraryAsync(DatabasePath, library.Id));
        _allCandidates.AddRange(loadedCandidates);
        NotifyCandidateCountsChanged(); ApplyCandidateFilter();
    }

    /// <summary>
    /// Candidate一覧を構築するためのDB同期と派生計算をUI Thread外で完結させる。
    /// </summary>
    /// <param name="databasePath">TrackMatch DatabaseのPath</param>
    /// <param name="libraryId">読み込み対象LibraryのID</param>
    private async Task<IReadOnlyList<CandidateReviewItemViewModel>> LoadCandidatesForLibraryAsync(
        string databasePath,
        long libraryId)
    {
        var database = new SqliteDatabase(databasePath);
        await database.InitializeAsync();

        // Human Verdict保存後の派生状態更新中にプロセスが終了しても、Current Human Verdictを正本として起動時に自己修復する。
        // Materialized Group/Keepをそのまま信頼して表示やファイル整理へ進まない。
        await new DuplicateGroupService(
            new SqliteCandidateReviewRepository(database),
            new SqliteTrackLookupRepository(database),
            new SqliteDuplicateGroupRepository(database))
            .SynchronizeGlobalAsync();

        // 前回終了時やLibrary状態変化後にKeep候補が複数残っていても、次の比較手段が無い状態を起動後へ持ち越さない。
        await EnsureSupplementalCandidatesAsync(database, libraryId);
        return await LoadCandidateItemsAsync(database, libraryId);
    }

    /// <summary>
    /// Candidate ReportとDuplicate Group Projectionを同じ一覧更新単位で取得し、UI公開前に派生状態を確定する。
    /// </summary>
    private static async Task<IReadOnlyList<CandidateReviewItemViewModel>> LoadCandidateItemsAsync(
        SqliteDatabase database,
        long libraryId)
    {
        var rows = await new SqliteCandidateReviewReportRepository(database).GetAsync(libraryId);
        var groups = await new SqliteDuplicateGroupRepository(database).GetByLibraryIdAsync(libraryId);
        var reviews = await GetUsableReviewsAsync(database);

        // CandidateごとのGroup検索は候補数に比例したDBアクセスになるため、派生計算に必要なCurrent Stateを一括取得する。
        // 取得に失敗した場合は例外を伝播し、レビュー省略を判定できない一覧をフェイルオープンで表示しない。
        var states = CandidateReviewPresentationStateResolver.Resolve(rows, groups, reviews);
        return rows
            .Select(row => new CandidateReviewItemViewModel(
                row,
                states[CandidatePairKey.Create(row.TrackIdA, row.TrackIdB)]))
            .ToArray();
    }

    private void ApplyCandidateFilter()
    {
        var library = SelectedLibrary; var previous = SelectedCandidate;
        var visible = ReviewTargetCandidates.Where(item => CandidateListMode switch
        {
            CandidateReviewListMode.Unreviewed => !item.IsReviewed && !item.IsReviewSkipped,
            CandidateReviewListMode.Reviewed => item.IsReviewed,
            CandidateReviewListMode.ReReviewRecommended => item.IsReviewed && !item.IsHumanVerdictSuspended && item.IsReReviewRecommended,
            _ => true,
        }).ToArray();
        if (previous is not null && !visible.Any(item => SameCandidate(item, previous)))
        {
            StopPlayback();
        }

        Candidates.Clear(); foreach (var item in visible)
        {
            Candidates.Add(item);
        }

        CandidateReviewItemViewModel? selection = null;
        if (library is not null && _sessionSelections.TryGetValue(library.Id, out var remembered))
        {
            selection = Candidates.FirstOrDefault(item => item.TrackIdA == remembered.TrackIdA && item.TrackIdB == remembered.TrackIdB);
        }

        if (selection is null && previous is not null)
        {
            selection = Candidates.FirstOrDefault(item => SameCandidate(item, previous));
        }

        SelectedCandidate = selection ?? Candidates.FirstOrDefault();
        if (library is not null)
        {
            StatusText = $"{library.Name} — 表示 {Candidates.Count} / 未レビュー {UnreviewedCount}件";
        }
    }

    private async Task SaveReviewAsync(CandidateReviewDecision decision, long? preferredTrackId)
    {
        var selected = SelectedCandidate; if (selected is null || !CanReview)
        {
            return;
        }

        var library = SelectedLibrary; if (library is null)
        {
            return;
        }

        StopPlayback(); IsLoading = true;
        try
        {
            var review = new CandidateReview(
                CandidatePairKey.Create(selected.TrackIdA, selected.TrackIdB),
                decision,
                decision == CandidateReviewDecision.ConfirmedDuplicate ? preferredTrackId : null,
                null);
            // Review保存はGlobal Group再同期と補完Candidate生成を伴うため、UI Threadから分離する。
            await Task.Run(() => SaveReviewAndRefreshDerivedStateAsync(DatabasePath, library.Id, review));
            await ReloadCandidatesPreservingPairAsync(selected.TrackIdA, selected.TrackIdB);
        }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// Human Verdictを保存し、そこから派生するGroupと補完CandidateをUI Thread外で更新する。
    /// </summary>
    private async Task SaveReviewAndRefreshDerivedStateAsync(string databasePath, long libraryId, CandidateReview review)
    {
        var database = new SqliteDatabase(databasePath);
        await database.InitializeAsync();
        var service = new DuplicateGroupService(
            new SqliteCandidateReviewRepository(database),
            new SqliteTrackLookupRepository(database),
            new SqliteDuplicateGroupRepository(database));
        await service.SaveReviewAsync(libraryId, review);
        await EnsureSupplementalCandidatesAsync(database, libraryId);
    }

    /// <summary>
    /// Human Verdictを解除し、残ったVerdictから派生状態と補完CandidateをUI Thread外で再構築する。
    /// </summary>
    private async Task DeleteReviewAndRefreshDerivedStateAsync(string databasePath, long libraryId, CandidatePairKey pair)
    {
        var database = new SqliteDatabase(databasePath);
        await database.InitializeAsync();
        var service = new DuplicateGroupService(
            new SqliteCandidateReviewRepository(database),
            new SqliteTrackLookupRepository(database),
            new SqliteDuplicateGroupRepository(database));
        await service.DeleteReviewAsync(libraryId, pair);
        await EnsureSupplementalCandidatesAsync(database, libraryId);
    }

    /// <summary>
    /// Trashの判定、実ファイル移動、移動後のGlobal Group再同期をUI Thread外で完結させる。
    /// </summary>
    private static async Task<RejectedTrackTrashResult> ProcessTrashCoreAsync(
        string databasePath,
        long libraryId,
        string trashRoot,
        bool execute,
        TrashDestinationCollisionBehavior collisionBehavior)
    {
        var database = new SqliteDatabase(databasePath);
        await database.InitializeAsync();
        var reviews = new SqliteCandidateReviewRepository(database);
        var trackLookup = new SqliteTrackLookupRepository(database);
        var groups = new SqliteDuplicateGroupRepository(database);
        var groupService = new DuplicateGroupService(reviews, trackLookup, groups);

        // 実ファイルを動かす直前にGlobal Verdictからグループを再同期し、古い派生状態を削除根拠にしない。
        await groupService.SynchronizeGlobalAsync();

        var tracks = new SqliteTrackRepository(database);
        var service = new RejectedTrackTrashService(groups, trackLookup, tracks, new LocalTrackFileOperations());
        var result = await service.ProcessAsync(libraryId, trashRoot, execute, collisionBehavior);
        if (execute && result.MovedCount > 0)
        {
            // 物理移動でTrackがMissingへ変わるため、Current VerdictからGlobal Groupを再構築する。
            await groupService.SynchronizeGlobalAsync();
        }

        return result;
    }

    /// <summary>
    /// 現在の優劣関係だけではKeepを一意化できないGroupへ、必要最小限の補完Candidateを生成する。
    /// </summary>
    private async Task EnsureSupplementalCandidatesAsync(SqliteDatabase database, long libraryId)
    {
        var reviews = await GetUsableReviewsAsync(database);
        var groups = await new SqliteDuplicateGroupRepository(database).GetByLibraryIdAsync(libraryId);
        var pairs = new SqliteCandidatePairRepository(database, libraryId);
        var existingPairs = await pairs.GetAllAsync();
        var required = new List<CandidatePairKey>();

        foreach (var group in groups.Where(group => group.KeepStatus == DuplicateGroupKeepStatus.Unselected))
        {
            var groupTrackIds = group.GlobalTrackIds.ToHashSet();
            var groupReviews = reviews
                .Where(review => groupTrackIds.Contains(review.Pair.TrackIdA)
                    && groupTrackIds.Contains(review.Pair.TrackIdB))
                .ToArray();
            var pair = ReviewNecessityEvaluator.FindSupplementalPair(group.TrackIds, groupReviews, existingPairs);
            if (pair is { } supplemental)
            {
                required.Add(supplemental);
            }
        }

        var existingKeys = existingPairs
            .Select(pair => CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB))
            .ToHashSet();
        var created = false;
        foreach (var pair in required.Where(pair => !existingKeys.Contains(pair)))
        {
            await pairs.EnsureSupplementalAsync(pair);
            created = true;
        }

        await pairs.DeleteObsoleteSupplementalAsync(required);

        // Pair保存後、比較生成前にアプリが終了した場合でも次回起動で補完Candidateを復旧する。
        // 「今回作成したか」だけで判定すると、DBにPairだけ残った状態が永久にUIへ現れないため、
        // 現在必要なPairにCurrent Comparisonが存在するかも確認する。
        var comparedKeys = (await new SqliteCandidateComparisonRepository(database, libraryId).GetAllAsync())
            .Select(comparison => CandidatePairKey.Create(comparison.TrackIdA, comparison.TrackIdB))
            .ToHashSet();
        var needsAnalysis = created || required.Any(pair => !comparedKeys.Contains(pair));

        // 比較保存後から分類保存前の間に終了したケースも自己修復する。
        // Review ReportはClassificationをLEFT JOINするため表示自体は可能だが、分類なしのまま恒久化すると
        // Machine Resultと再確認判定が欠落するので、必要な補完Pairの分類有無も起動時に確認する。
        var classifiedKeys = await new SqliteCandidateClassificationRepository(database, libraryId)
            .GetClassifiedPairKeysAsync();
        var needsClassification = required.Any(pair => comparedKeys.Contains(pair) && !classifiedKeys.Contains(pair));
        if (!needsAnalysis && !needsClassification)
        {
            return;
        }

        var fpcalcPath = Environment.GetEnvironmentVariable("TRACKMATCH_FPCALC") ?? "fpcalc";
        var workflow = new LibraryAnalysisWorkflow(DatabasePath, fpcalcPath);
        if (needsAnalysis)
        {
            await workflow.AnalyzeCandidatesAsync(libraryId);
        }

        // 補完Candidateも通常Candidateと同じMachine Resultを持たせる。
        // 分類だけ欠けた中断状態では不要なFingerprint比較を繰り返さず、分類処理だけを再実行する。
        await workflow.ClassifyCandidatesAsync(libraryId);
    }

    /// <summary>
    /// Content Verification中のTrackに関係するVerdictを除き、現在の派生計算へ利用可能なHuman Verdictだけを取得する。
    /// </summary>
    private static async Task<IReadOnlyList<CandidateReview>> GetUsableReviewsAsync(SqliteDatabase database)
    {
        var reviews = await new SqliteCandidateReviewRepository(database).GetAllAsync();
        var tracks = new SqliteTrackLookupRepository(database);
        var result = new List<CandidateReview>(reviews.Count);
        var usableByTrackId = new Dictionary<long, bool>();

        foreach (var review in reviews)
        {
            if (!usableByTrackId.TryGetValue(review.Pair.TrackIdA, out var usableA))
            {
                usableA = await tracks.IsHumanVerdictUsableAsync(review.Pair.TrackIdA);
                usableByTrackId.Add(review.Pair.TrackIdA, usableA);
            }

            if (!usableByTrackId.TryGetValue(review.Pair.TrackIdB, out var usableB))
            {
                usableB = await tracks.IsHumanVerdictUsableAsync(review.Pair.TrackIdB);
                usableByTrackId.Add(review.Pair.TrackIdB, usableB);
            }

            if (usableA && usableB)
            {
                result.Add(review);
            }
        }

        return result;
    }

    /// <summary>
    /// Conflict一覧など別画面から指定Pairの通常レビュー位置へ移動する。
    /// </summary>
    public async Task NavigateToCandidateAsync(CandidatePairKey pair)
    {
        CandidateListMode = CandidateReviewListMode.All;
        await ReloadCandidatesPreservingPairAsync(pair.TrackIdA, pair.TrackIdB);
        SelectedCandidate = Candidates.FirstOrDefault(item =>
            CandidatePairKey.Create(item.TrackIdA, item.TrackIdB) == pair);
    }

    private async Task ReloadCandidatesPreservingPairAsync(long trackIdA, long trackIdB)
    {
        var library = SelectedLibrary; if (library is null)
        {
            return;
        }

        var items = await Task.Run(async () =>
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            return await LoadCandidateItemsAsync(database, library.Id);
        });
        _allCandidates.Clear(); _allCandidates.AddRange(items);
        NotifyCandidateCountsChanged(); ApplyCandidateFilter();
        SelectedCandidate = Candidates.FirstOrDefault(item => item.TrackIdA == trackIdA && item.TrackIdB == trackIdB) ?? SelectedCandidate;
    }

    private void NotifyCandidateCountsChanged()
    {
        OnPropertyChanged(nameof(UnreviewedCount));
        OnPropertyChanged(nameof(ReviewedCount));
        OnPropertyChanged(nameof(SuspendedReviewCount));
        OnPropertyChanged(nameof(ReviewedTabHeader));
        OnPropertyChanged(nameof(ReReviewRecommendedCount));
        OnPropertyChanged(nameof(TotalCandidateCount));
    }

    private async Task SaveSettingsSafeAsync()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        _settings = new TrackMatchAppSettings(SelectedLibrary?.Id, SimilarityDisplayLowerBoundPercent, string.IsNullOrWhiteSpace(TrashRoot) ? null : TrashRoot, _settings.DetailedLogging);
        try { await _settingsStore.SaveAsync(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { StatusText = $"設定保存失敗: {exception.Message}"; }
    }

    private static bool SameCandidate(CandidateReviewItemViewModel left, CandidateReviewItemViewModel right) => left.TrackIdA == right.TrackIdA && left.TrackIdB == right.TrackIdB;

    private static string FormatAnalysisProgress(LibraryAnalysisProgress progress)
    {
        var detail = string.IsNullOrWhiteSpace(progress.Detail) ? string.Empty : $" {progress.Detail}";
        var count = progress.TotalCount is { } total ? $" — {progress.CompletedCount:N0} / {total:N0}" : progress.CompletedCount > 0 ? $" — {progress.CompletedCount:N0}件処理済み" : string.Empty;
        return progress.Stage switch
        {
            LibraryAnalysisStage.Scanning => FormatScanProgress(progress),
            LibraryAnalysisStage.GeneratingCandidates => $"候補生成中:{detail}{count}",
            LibraryAnalysisStage.AnalyzingCandidates => $"詳細比較中:{detail}{count}{(progress.TotalCount is not null ? "件" : string.Empty)}",
            _ => "分析中...",
        };
    }

    /// <summary>
    /// スキャン中のフォルダー位置と、そのフォルダー内の曲処理数を区別して表示する。
    /// </summary>
    private static string FormatScanProgress(LibraryAnalysisProgress progress)
    {
        var folder = string.IsNullOrWhiteSpace(progress.Detail)
            ? string.Empty
            : $" {progress.Detail.Replace("対象フォルダ ", "フォルダー ", StringComparison.Ordinal)}";
        var tracks = progress.TotalCount is { } total
            ? $"　曲 {progress.CompletedCount:N0}/{total:N0}"
            : progress.CompletedCount > 0
                ? $"　{progress.CompletedCount:N0}曲処理済み"
                : string.Empty;
        return $"スキャン中:{folder}{tracks}";
    }

    private string FormatAnalysisSummary(string prefix, IReadOnlyCollection<ScanSessionSummary> summaries)
    {
        var total = summaries.Sum(item => item.TotalFiles); var added = summaries.Sum(item => item.AddedFiles); var updated = summaries.Sum(item => item.UpdatedFiles); var missing = summaries.Sum(item => item.RemovedFiles); var errors = summaries.Sum(item => item.ErrorCount);
        return $"{prefix}{(errors > 0 ? "（エラーあり）" : string.Empty)} — 対象 {total:N0}曲 / 新規 {added:N0} / 更新 {updated:N0} / 見つからない音源 {missing:N0} / エラー {errors:N0}";
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
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanReview)); OnPropertyChanged(nameof(CanClearReview)); OnPropertyChanged(nameof(CanAnalyzeLibrary)); OnPropertyChanged(nameof(CanCancelAnalysis)); OnPropertyChanged(nameof(CanManageLibraries)); OnPropertyChanged(nameof(CanProcessTrash));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value; OnPropertyChanged(propertyName); return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>候補一覧で表示するレビュー状態を表す。</summary>
public enum CandidateReviewListMode
{
    Unreviewed,
    Reviewed,
    ReReviewRecommended,
    All,
}
