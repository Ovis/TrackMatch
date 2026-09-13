using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TrackMatch.App.Playback;
using TrackMatch.Core.Candidates;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App;

/// <summary>
/// 確定済み重複グループの構成、残すファイル、確認根拠を参照する詳細画面。
/// </summary>
public partial class DuplicateGroupDetailsWindow : Window, INotifyPropertyChanged
{
    private readonly string _databasePath;
    private readonly long _groupId;
    private readonly SingleTrackPreviewPlayer _previewPlayer = new();
    private Button? _playingButton;
    private string _groupTitle = "重複グループ";
    private string _fileCountText = string.Empty;

    public DuplicateGroupDetailsWindow(string databasePath, long groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (groupId <= 0) throw new ArgumentOutOfRangeException(nameof(groupId));

        _databasePath = databasePath;
        _groupId = groupId;
        InitializeComponent();
        DataContext = this;
        _previewPlayer.PlaybackStopped += PreviewPlayer_PlaybackStopped;
        Loaded += DuplicateGroupDetailsWindow_Loaded;
        Closed += DuplicateGroupDetailsWindow_Closed;
    }

    public string GroupTitle
    {
        get => _groupTitle;
        private set
        {
            if (_groupTitle == value) return;
            _groupTitle = value;
            OnPropertyChanged();
        }
    }

    public string FileCountText
    {
        get => _fileCountText;
        private set
        {
            if (_fileCountText == value) return;
            _fileCountText = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<DuplicateGroupTrackViewModel> KeepTracks { get; } = [];
    public ObservableCollection<DuplicateGroupTrackViewModel> OtherTracks { get; } = [];
    public ObservableCollection<string> ConfirmedRelations { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    private async void DuplicateGroupDetailsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DuplicateGroupDetailsWindow_Loaded;
        try
        {
            var database = new SqliteDatabase(_databasePath);
            await database.InitializeAsync();
            var groups = new SqliteDuplicateGroupRepository(database);
            var group = await groups.GetByIdAsync(_groupId);
            if (group is null)
            {
                throw new InvalidOperationException("指定された重複グループは既に存在しません。");
            }

            var trackLookup = new SqliteTrackLookupRepository(database);
            var trackModels = new Dictionary<long, DuplicateGroupTrackViewModel>();
            foreach (var trackId in group.TrackIds)
            {
                var track = await trackLookup.GetByIdAsync(trackId)
                    ?? throw new InvalidDataException($"重複グループ内のTrack #{trackId} が見つかりません。");
                if (track.LibraryId != group.LibraryId)
                {
                    throw new InvalidDataException("重複グループに異なるLibraryのTrackが含まれています。");
                }

                trackModels[trackId] = DuplicateGroupTrackViewModel.Create(track, trackId == group.KeepTrackId);
            }

            GroupTitle = $"重複グループ #{group.Id}";
            FileCountText = $"{group.TrackIds.Count}ファイル";
            KeepTracks.Add(trackModels[group.KeepTrackId]);
            foreach (var item in group.TrackIds
                         .Where(trackId => trackId != group.KeepTrackId)
                         .Select(trackId => trackModels[trackId]))
            {
                OtherTracks.Add(item);
            }

            var memberIds = group.TrackIds.ToHashSet();
            var reviews = await new SqliteCandidateReviewRepository(database).GetAllAsync();
            foreach (var review in reviews
                         .Where(review => review.Decision == CandidateReviewDecision.ConfirmedDuplicate
                             && memberIds.Contains(review.Pair.TrackIdA)
                             && memberIds.Contains(review.Pair.TrackIdB))
                         .OrderBy(review => review.Pair.TrackIdA)
                         .ThenBy(review => review.Pair.TrackIdB))
            {
                var left = trackModels[review.Pair.TrackIdA].Title;
                var right = trackModels[review.Pair.TrackIdB].Title;
                ConfirmedRelations.Add($"{left} ↔ {right}    重複として確認済み");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            new ConfirmationDialog(
                "重複グループ読み込み失敗",
                "重複グループの詳細を表示できませんでした",
                exception.Message,
                "閉じる",
                kind: AppDialogKind.Error)
            { Owner = this }.ShowDialog();
            Close();
        }
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string path)
        {
            return;
        }

        try
        {
            if (_playingButton == button && string.Equals(_previewPlayer.PlayingPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                _previewPlayer.Stop();
                return;
            }

            ResetPlayingButton();
            _previewPlayer.Play(path);
            _playingButton = button;
            button.Content = "■ 停止";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or DllNotFoundException)
        {
            ResetPlayingButton();
            new ConfirmationDialog(
                "試聴失敗",
                "音源を再生できませんでした",
                exception.Message,
                "閉じる",
                kind: AppDialogKind.Error)
            { Owner = this }.ShowDialog();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string path)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{Path.GetFullPath(path)}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or ArgumentException)
        {
            new ConfirmationDialog(
                "フォルダ表示失敗",
                "エクスプローラーでファイルを表示できませんでした",
                exception.Message,
                "閉じる",
                kind: AppDialogKind.Error)
            { Owner = this }.ShowDialog();
        }
    }

    private void PreviewPlayer_PlaybackStopped(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(ResetPlayingButton);

    private void ResetPlayingButton()
    {
        if (_playingButton is not null)
        {
            _playingButton.Content = "▶ 再生";
            _playingButton = null;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void DuplicateGroupDetailsWindow_Closed(object? sender, EventArgs e)
    {
        _previewPlayer.PlaybackStopped -= PreviewPlayer_PlaybackStopped;
        _previewPlayer.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
