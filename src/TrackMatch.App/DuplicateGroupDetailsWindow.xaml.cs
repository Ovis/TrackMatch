using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TrackMatch.App.Playback;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Trash;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Trash;

namespace TrackMatch.App;

/// <summary>
/// Global Duplicate Groupの構成、Library固有Keep、確認根拠と明示的なGlobal Track操作を扱う詳細画面。
/// </summary>
public partial class DuplicateGroupDetailsWindow : Window, INotifyPropertyChanged
{
    private readonly string _databasePath;
    private readonly long _libraryId;
    private readonly long _groupId;
    private readonly string _trashRoot;
    private readonly SingleTrackPreviewPlayer _previewPlayer = new();
    private Button? _playingButton;
    private string _groupTitle = "重複グループ";
    private string _fileCountText = string.Empty;
    private string _keepStateText = string.Empty;
    private DuplicateGroupTrackViewModel? _selectedTrack;

    /// <summary>
    /// 指定Libraryから見たGlobal Duplicate Group詳細画面を生成する。
    /// </summary>
    /// <param name="databasePath">TrackMatchのSQLiteデータベースパス</param>
    /// <param name="libraryId">現在画面で選択しているLibrary</param>
    /// <param name="groupId">表示対象のGlobal Duplicate Group</param>
    /// <param name="trashRoot">App-wide Trashのルート。未設定の場合は物理Trash操作を案内だけに留める</param>
    public DuplicateGroupDetailsWindow(string databasePath, long libraryId, long groupId, string trashRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (libraryId <= 0) throw new ArgumentOutOfRangeException(nameof(libraryId));
        if (groupId <= 0) throw new ArgumentOutOfRangeException(nameof(groupId));

        _databasePath = databasePath;
        _libraryId = libraryId;
        _groupId = groupId;
        _trashRoot = trashRoot ?? string.Empty;
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

    public string KeepStateText
    {
        get => _keepStateText;
        private set
        {
            if (_keepStateText == value) return;
            _keepStateText = value;
            OnPropertyChanged();
        }
    }

    public DuplicateGroupTrackViewModel? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (ReferenceEquals(_selectedTrack, value)) return;
            _previewPlayer.Stop();
            _selectedTrack = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<DuplicateGroupTrackViewModel> AllTracks { get; } = [];
    public ObservableCollection<string> ConfirmedRelations { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    private async void DuplicateGroupDetailsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DuplicateGroupDetailsWindow_Loaded;
        try
        {
            await LoadGroupAsync();
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

    private async Task LoadGroupAsync()
    {
        var previouslySelectedTrackId = SelectedTrack?.TrackId;
        var database = new SqliteDatabase(_databasePath);
        await database.InitializeAsync();
        var groups = new SqliteDuplicateGroupRepository(database);
        var group = await groups.GetByIdAsync(_groupId, _libraryId);
        if (group is null)
        {
            throw new InvalidOperationException("指定された重複グループは現在のLibraryから参照できません。");
        }

        var trackLookup = new SqliteTrackLookupRepository(database);
        var localTrackIds = group.TrackIds.ToHashSet();
        var trackModels = new Dictionary<long, DuplicateGroupTrackViewModel>();
        foreach (var trackId in group.GlobalTrackIds)
        {
            var track = await trackLookup.GetByIdAsync(trackId)
                ?? throw new InvalidDataException($"重複グループ内のTrack #{trackId} が見つかりません。");
            trackModels[trackId] = DuplicateGroupTrackViewModel.Create(
                track,
                group.KeepTrackId == trackId,
                localTrackIds.Contains(trackId));
        }

        GroupTitle = $"重複グループ #{group.Id}";
        var externalCount = group.GlobalTrackIds.Count - group.TrackIds.Count;
        FileCountText = externalCount > 0
            ? $"Track数: {group.GlobalTrackIds.Count}（現在のLibrary: {group.TrackIds.Count} / Library外: {externalCount}）"
            : $"Track数: {group.TrackIds.Count}（すべて現在のLibrary）";
        KeepStateText = group.KeepStatus switch
        {
            DuplicateGroupKeepStatus.Selected => "このLibraryのKeepは確定済みです。",
            DuplicateGroupKeepStatus.Conflict => "複数の過去Keepが競合しています。残すファイルを再確認してください。",
            DuplicateGroupKeepStatus.Missing => "以前残すよう指定したファイルが見つかりません。残すファイルを再確認してください。",
            _ => "残すファイルはまだ選択されていません。",
        };

        AllTracks.Clear();
        ConfirmedRelations.Clear();
        foreach (var item in group.GlobalTrackIds.Select(trackId => trackModels[trackId]))
        {
            AllTracks.Add(item);
        }

        var globalMemberIds = group.GlobalTrackIds.ToHashSet();
        var reviews = await new SqliteCandidateReviewRepository(database).GetAllAsync();
        foreach (var review in reviews
                     .Where(review => review.Decision == CandidateReviewDecision.ConfirmedDuplicate
                         && globalMemberIds.Contains(review.Pair.TrackIdA)
                         && globalMemberIds.Contains(review.Pair.TrackIdB))
                     .OrderBy(review => review.Pair.TrackIdA)
                     .ThenBy(review => review.Pair.TrackIdB))
        {
            var left = trackModels[review.Pair.TrackIdA].Title;
            var right = trackModels[review.Pair.TrackIdB].Title;
            ConfirmedRelations.Add($"{left} ↔ {right}    重複として確認済み");
        }

        SelectedTrack = previouslySelectedTrackId is { } selectedId
            ? AllTracks.FirstOrDefault(item => item.TrackId == selectedId) ?? AllTracks.FirstOrDefault()
            : AllTracks.FirstOrDefault(item => item.IsKeep) ?? AllTracks.FirstOrDefault();
    }

    private async void SetSelectedKeep_Click(object sender, RoutedEventArgs e)
    {
        var track = SelectedTrack;
        if (track is null || !track.CanSelectAsKeep)
        {
            return;
        }

        var detail = track.IsInCurrentLibrary
            ? "このLibraryの重複グループで、このファイル以外がごみ箱移動対象になります。"
            : "このファイルは現在のLibrary外ですが、Global Duplicate Groupの構成TrackなのでKeepとして選択できます。Library Membership自体は追加されません。";
        var confirmation = new ConfirmationDialog(
            "残すファイルを変更",
            $"「{track.Title}」をこのLibraryで残すファイルに設定しますか？",
            detail,
            "このファイルを残す",
            "キャンセル",
            kind: AppDialogKind.Warning)
        { Owner = this };
        confirmation.ShowDialog();
        if (confirmation.SelectedResult != AppDialogResult.Primary)
        {
            return;
        }

        try
        {
            _previewPlayer.Stop();
            var database = new SqliteDatabase(_databasePath);
            await database.InitializeAsync();
            var groups = new SqliteDuplicateGroupRepository(database);
            await groups.SetKeepAsync(_libraryId, _groupId, track.TrackId, "UserSelected");
            await LoadGroupAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ShowError("残すファイル変更失敗", "残すファイルを変更できませんでした", exception.Message);
        }
    }

    private async void TrashSelectedTrack_Click(object sender, RoutedEventArgs e)
    {
        var track = SelectedTrack;
        if (track is null || !track.CanTrashGlobally)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_trashRoot))
        {
            new ConfirmationDialog(
                "ごみ箱フォルダ未設定",
                "ごみ箱フォルダが設定されていません",
                "メイン画面のLibrary設定からApp-wideのごみ箱フォルダを設定してから実行してください。",
                "閉じる",
                kind: AppDialogKind.Warning)
            { Owner = this }.ShowDialog();
            return;
        }

        try
        {
            _previewPlayer.Stop();
            var database = new SqliteDatabase(_databasePath);
            await database.InitializeAsync();
            var reviews = new SqliteCandidateReviewRepository(database);
            var trackLookup = new SqliteTrackLookupRepository(database);
            var groups = new SqliteDuplicateGroupRepository(database);
            var tracks = new SqliteTrackRepository(database);
            var service = new RejectedTrackTrashService(groups, trackLookup, tracks, new LocalTrackFileOperations());

            var preview = await service.ProcessTrackGloballyAsync(_libraryId, track.TrackId, _trashRoot, execute: false);
            var previewItem = preview.Items.SingleOrDefault();
            if (previewItem is null)
            {
                ShowError("ごみ箱処理失敗", "対象Trackを確認できませんでした", track.Path);
                return;
            }

            var impact = preview.SharedTrackImpacts.SingleOrDefault();
            var detail = "このTrackは現在のLibraryには所属していません。物理ファイルを移動するGlobal操作であり、Library Membershipを追加する操作ではありません。";
            if (impact is not null && impact.OtherLibraries.Count > 0)
            {
                detail += $"\n\n参照中の他Library: {string.Join("、", impact.OtherLibraries.Select(item => item.Name))}";
            }
            if (impact is not null && impact.KeepLibraries.Count > 0)
            {
                detail += $"\n\n警告: {string.Join("、", impact.KeepLibraries.Select(item => item.Name))} ではこのTrackがKeepに指定されています。移動後はKeep不在となり再確認が必要です。";
            }

            var confirmation = new ConfirmationDialog(
                "Library外Trackをごみ箱へ移動",
                $"「{track.Title}」の物理ファイルをごみ箱へ移動しますか？",
                detail,
                "影響を確認して続行",
                "キャンセル",
                kind: AppDialogKind.Warning)
            { Owner = this };
            confirmation.ShowDialog();
            if (confirmation.SelectedResult != AppDialogResult.Primary)
            {
                return;
            }

            var collisionBehavior = TrashDestinationCollisionBehavior.Skip;
            if (previewItem.Status == RejectedTrackMoveStatus.DestinationExists)
            {
                var collisionDialog = new ConfirmationDialog(
                    "移動先のファイル重複",
                    "ごみ箱側に同じパスのファイルがあります",
                    "別名で移動するか、このTrackの移動を中止するかを選択してください。",
                    "別名で移動",
                    "中止",
                    kind: AppDialogKind.Warning)
                { Owner = this };
                collisionDialog.ShowDialog();
                if (collisionDialog.SelectedResult != AppDialogResult.Primary)
                {
                    return;
                }
                collisionBehavior = TrashDestinationCollisionBehavior.Rename;
            }
            else if (previewItem.Status != RejectedTrackMoveStatus.Ready)
            {
                ShowError("ごみ箱へ移動できません", previewItem.Message ?? "このTrackは現在移動できません。", track.Path);
                return;
            }

            var result = await service.ProcessTrackGloballyAsync(
                _libraryId,
                track.TrackId,
                _trashRoot,
                execute: true,
                collisionBehavior);
            var resultItem = result.Items.SingleOrDefault();
            if (resultItem?.Status != RejectedTrackMoveStatus.Moved)
            {
                ShowError("ごみ箱への移動失敗", resultItem?.Message ?? "ファイルを移動できませんでした。", track.Path);
                return;
            }

            // 物理移動後はTrackがMissingになるため、Global GroupもCurrent Verdictから再構成する。
            var groupService = new DuplicateGroupService(reviews, trackLookup, groups);
            await groupService.SynchronizeGlobalAsync();

            var refreshed = await groups.GetByIdAsync(_groupId, _libraryId);
            if (refreshed is null)
            {
                Close();
                return;
            }

            await LoadGroupAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ShowError("ごみ箱処理失敗", "Library外Trackのごみ箱処理を完了できませんでした", exception.Message);
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
            ShowError("試聴失敗", "音源を再生できませんでした", exception.Message);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string path || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                // Main Windowと同じく対象ファイルをExplorer上で選択し、どのファイルを見ていたか分からなくならないようにする。
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
                return;
            }

            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true,
                });
                return;
            }

            new ConfirmationDialog(
                "フォルダーを開く",
                "ファイルまたは保存先フォルダーが見つかりません",
                path,
                "閉じる",
                kind: AppDialogKind.Warning)
            { Owner = this }.ShowDialog();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or ArgumentException)
        {
            ShowError("フォルダーを開けませんでした", "エクスプローラーを起動できませんでした", exception.Message);
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

    private void ShowError(string title, string message, string detail)
    {
        new ConfirmationDialog(
            title,
            message,
            detail,
            "閉じる",
            kind: AppDialogKind.Error)
        { Owner = this }.ShowDialog();
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
