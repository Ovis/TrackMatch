using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TrackMatch.Core.Candidates;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App;

/// <summary>
/// 候補レビュー画面の状態と永続化処理を管理する。
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string _databasePath = string.Empty;
    private CandidateReviewItemViewModel? _selectedCandidate;
    private string _statusText = "SQLiteデータベースを選択してください。";
    private bool _isBusy;

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
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            StatusText = "SQLiteデータベースを選択してください。";
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

    public Task MarkNotDuplicateAsync()
        => SaveReviewAsync(CandidateReviewDecision.NotDuplicate, null);

    public Task ConfirmDuplicateKeepAAsync()
        => SaveReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdA);

    public Task ConfirmDuplicateKeepBAsync()
        => SaveReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdB);

    private async Task SaveReviewAsync(CandidateReviewDecision decision, long? keepTrackId)
    {
        var selected = SelectedCandidate;
        if (selected is null || string.IsNullOrWhiteSpace(DatabasePath))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            var repository = new SqliteCandidateReviewRepository(database);
            var review = new CandidateReview(
                CandidatePairKey.Create(selected.TrackIdA, selected.TrackIdB),
                decision,
                note: null,
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
