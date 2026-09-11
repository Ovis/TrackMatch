using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using TrackMatch.App.Playback;
using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Trash;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Trash;

namespace TrackMatch.App;

/// <summary>
/// 候補レビュー画面の状態と永続化処理を管理する。
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ITrackPlaybackService _playbackService;
    private string _databasePath = TrackMatchDataPaths.DefaultDatabasePath;
    private string _libraryRoot = string.Empty;
    private string _trashRoot = string.Empty;
    private CandidateReviewItemViewModel? _selectedCandidate;
    private string _statusText = "候補を読み込んでいます。";
    private string _analysisStatusText = "ライブラリ分析は未実行";
    private string _playbackStatusText = "停止中";
    private string _trashStatusText = "Trash処理は未実行";
    private bool _isBusy;
    private bool _disposed;

    /// <summary>
    /// 候補レビュー画面のViewModelを生成する。
    /// </summary>
    /// <param name="playbackService">A/B比較に使用する単一トラック再生サービス</param>
    public MainWindowViewModel(ITrackPlaybackService playbackService)
    {
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _playbackService.PlaybackEnded += OnPlaybackEnded;
        _playbackService.PlaybackFailed += OnPlaybackFailed;
    }

    public ObservableCollection<CandidateReviewItemViewModel> Candidates { get; } = [];

    public string DatabasePath
    {
        get => _databasePath;
        set
        {
            if (_databasePath == value)
            {
                return;
            }

            _databasePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAnalyzeLibrary));
            OnPropertyChanged(nameof(CanProcessTrash));
        }
    }

    public string LibraryRoot
    {
        get => _libraryRoot;
        set
        {
            if (_libraryRoot == value)
            {
                return;
            }

            _libraryRoot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAnalyzeLibrary));
            OnPropertyChanged(nameof(CanProcessTrash));
        }
    }

    public string TrashRoot
    {
        get => _trashRoot;
        set
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

    public CandidateReviewItemViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (ReferenceEquals(_selectedCandidate, value))
            {
                return;
            }

            // 候補を切り替えたあとに前の候補の音声が流れ続けると、A/Bの対応を誤認しやすいため必ず停止する。
            StopPlayback();
            _selectedCandidate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedCandidate is not null && !IsBusy;

    public bool CanAnalyzeLibrary => !IsBusy
        && !string.IsNullOrWhiteSpace(DatabasePath)
        && !string.IsNullOrWhiteSpace(LibraryRoot);

    public bool CanProcessTrash => !IsBusy
        && !string.IsNullOrWhiteSpace(LibraryRoot)
        && !string.IsNullOrWhiteSpace(TrashRoot)
        && File.Exists(DatabasePath);

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string AnalysisStatusText
    {
        get => _analysisStatusText;
        private set
        {
            if (_analysisStatusText == value)
            {
                return;
            }

            _analysisStatusText = value;
            OnPropertyChanged();
        }
    }

    public string PlaybackStatusText
    {
        get => _playbackStatusText;
        private set
        {
            if (_playbackStatusText == value)
            {
                return;
            }

            _playbackStatusText = value;
            OnPropertyChanged();
        }
    }

    public string TrashStatusText
    {
        get => _trashStatusText;
        private set
        {
            if (_trashStatusText == value)
            {
                return;
            }

            _trashStatusText = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanAnalyzeLibrary));
            OnPropertyChanged(nameof(CanProcessTrash));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadAsync()
    {
        StopPlayback();

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            StatusText = "SQLiteデータベースを選択してください。";
            return;
        }

        if (!File.Exists(DatabasePath))
        {
            // GUIは通常の分析入口も持つため、DBが無い状態では空DBを作らずライブラリ指定を案内する。
            Candidates.Clear();
            SelectedCandidate = null;
            StatusText = "まだライブラリがスキャンされていません。Library Rootを指定してスキャン・分析を実行してください。";
            OnPropertyChanged(nameof(CanProcessTrash));
            return;
        }

        IsBusy = true;
        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            var rows = await new SqliteCandidateReviewReportRepository(database).GetAsync();

            Candidates.Clear();
            foreach (var row in rows)
            {
                Candidates.Add(new CandidateReviewItemViewModel(row));
            }

            SelectedCandidate = Candidates.FirstOrDefault();
            StatusText = $"未レビュー候補: {Candidates.Count}件";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            StatusText = $"読み込み失敗: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 指定ライブラリを増分スキャンし、候補生成と詳細比較まで一括実行する。
    /// </summary>
    public async Task AnalyzeLibraryAsync()
    {
        if (!CanAnalyzeLibrary)
        {
            AnalysisStatusText = "Library RootとDatabaseを指定してください。";
            return;
        }

        StopPlayback();
        IsBusy = true;
        var succeeded = false;
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

            var result = await workflow.RunAsync(LibraryRoot, progress);
            var scan = result.Scan.Summary;
            AnalysisStatusText =
                $"完了: Scan {scan.TotalFiles}曲 (追加 {scan.AddedFiles} / 更新 {scan.UpdatedFiles} / Error {scan.ErrorCount}) / " +
                $"候補更新 {result.Generation.Pairs.Count} / 詳細比較 {result.Analysis.ComparedCandidates} / 再利用 {result.Analysis.ReusedCandidates}";
            succeeded = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            AnalysisStatusText = $"分析失敗: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        if (succeeded)
        {
            // 分析結果を別操作なしで確認できるよう、詳細比較完了後に一覧も更新する。
            await LoadAsync();
        }
    }

    /// <summary>
    /// 選択中候補のTrack Aを再生する。
    /// </summary>
    public void PlayTrackA()
        => PlaySelectedTrack(isTrackA: true);

    /// <summary>
    /// 選択中候補のTrack Bを再生する。
    /// </summary>
    public void PlayTrackB()
        => PlaySelectedTrack(isTrackA: false);

    /// <summary>
    /// 現在の音声再生を停止する。
    /// </summary>
    public void StopPlayback()
    {
        _playbackService.Stop();
        PlaybackStatusText = "停止中";
    }

    public Task MarkNotDuplicateAsync()
        => SaveReviewAsync(CandidateReviewDecision.NotDuplicate, null);

    public Task ConfirmDuplicateKeepAAsync()
        => SaveReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdA);

    public Task ConfirmDuplicateKeepBAsync()
        => SaveReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdB);

    /// <summary>
    /// 重複レビューで破棄対象になったトラックについて、Trash移動の事前確認または実移動を行う。
    /// </summary>
    /// <param name="execute">trueの場合は実際に移動し、falseの場合はDry-runのみ行う</param>
    public async Task<RejectedTrackTrashResult?> ProcessTrashAsync(bool execute)
    {
        if (!CanProcessTrash)
        {
            TrashStatusText = "Library Root、Trash Root、既存DBを指定してください。";
            return null;
        }

        StopPlayback();
        IsBusy = true;
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
            var result = await service.ProcessAsync(LibraryRoot, TrashRoot, execute);

            TrashStatusText = execute
                ? $"Trash移動完了: {result.MovedCount}件 / Blocked: {result.BlockedCount}件"
                : $"Dry-run: 移動可能 {result.ReadyCount}件 / Blocked: {result.BlockedCount}件";
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            TrashStatusText = $"Trash処理失敗: {exception.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playbackService.PlaybackEnded -= OnPlaybackEnded;
        _playbackService.PlaybackFailed -= OnPlaybackFailed;
        _playbackService.Dispose();
    }

    private void PlaySelectedTrack(bool isTrackA)
    {
        var selected = SelectedCandidate;
        if (selected is null || IsBusy)
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
        if (selected is null || string.IsNullOrWhiteSpace(DatabasePath))
        {
            return;
        }

        // レビュー確定後は一覧から候補が消えるため、前候補の音声だけが残らないよう保存前に停止する。
        StopPlayback();
        IsBusy = true;
        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            var repository = new SqliteCandidateReviewRepository(database);
            var review = new CandidateReview(
                CandidatePairKey.Create(selected.TrackIdA, selected.TrackIdB),
                decision,
                Note: null,
                keepTrackId);
            await repository.SaveAsync(review);

            Candidates.Remove(selected);
            SelectedCandidate = Candidates.FirstOrDefault();
            StatusText = decision == CandidateReviewDecision.NotDuplicate
                ? $"NotDuplicateとして保存しました。残り {Candidates.Count}件"
                : $"ConfirmedDuplicateとして保存しました。Keep: {keepTrackId} / 残り {Candidates.Count}件";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            StatusText = $"保存失敗: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
        => PlaybackStatusText = "再生終了";

    private void OnPlaybackFailed(string message)
        => PlaybackStatusText = $"再生失敗: {message}";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
