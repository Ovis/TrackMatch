using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TrackMatch.App.Playback;
using TrackMatch.Core.Candidates;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App;

/// <summary>
/// 確定済み重複グループの構成、残すファイル、確認根拠を参照する詳細画面。
/// </summary>
public partial class DuplicateGroupDetailsWindow : Window
{
    private readonly string _databasePath;
    private readonly long _groupId;
    private readonly SingleTrackPreviewPlayer _previewPlayer = new();
    private Button? _playingButton;

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

    public string GroupTitle { get; private set; } = "重複グループ";
    public string FileCountText { get; private set; } = string.Empty;
    public ObservableCollection<DuplicateGroupTrackViewModel> KeepTracks { get; } = [];
    public ObservableCollection<DuplicateGroupTrackViewModel> OtherTracks { get; } = [];
    public ObservableCollection<string> ConfirmedRelations { get; } = [];

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
            var keep = trackModels[group.KeepTrackId];
            KeepTracks.Add(keep);
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

            OnPropertyChanged(nameof(GroupTitle));
            OnPropertyChanged(nameof(FileCountText));
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
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

    private void OnPropertyChanged(string propertyName)
        => Dispatcher.Invoke(() => System.ComponentModel.PropertyChangedEventManager.AddHandler(this, (_, _) => { }, propertyName));
}
