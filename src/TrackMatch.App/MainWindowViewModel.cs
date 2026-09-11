using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using TrackMatch.App.Playback;
using TrackMatch.Core.Candidates;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App;

/// <summary>
/// 候補レビュー画面の状態と永続化処理を管理する。
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ITrackPlaybackService _playbackService;
    private string _databasePath = TrackMatchDataPaths.DefaultDatabasePath;
    private CandidateReviewItemViewModel? _selectedCandidate;
    private string _statusText = "候補を読み込んでいます。";
    private string _playbackStatusText = "停止中";
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
            // GUIはレビュー済みデータを参照する役割に留め、初回DB作成はScannerのscanに任せる。
            // 先にGUIを起動しただけで空DBが生成されると、ライブラリ未登録なのか空なのか判別しづらくなるため作成しない。
            Candidates.Clear();
            SelectedCandidate = null;
            StatusText = "まだライブラリがスキャンされていません。Scannerで初回scanを実行してください。";
            return;
        }

        IsBusy = true;
        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            var rows = await new SqliteCandidateClassificationRepository(database).GetReportAsync();

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
