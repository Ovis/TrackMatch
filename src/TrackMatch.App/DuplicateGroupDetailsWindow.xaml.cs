using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TrackMatch.App.Playback;
using TrackMatch.App.Settings;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Trash;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Trash;

namespace TrackMatch.App;

/// <summary>
/// 重複グループの構成、ライブラリ固有の残すファイル、確認根拠と明示的な物理ファイル操作を扱う詳細画面。
/// </summary>
public partial class DuplicateGroupDetailsWindow : Window, INotifyPropertyChanged
{
    private readonly string _databasePath;
    private readonly long _libraryId;
    private readonly long _groupId;
    private readonly List<CandidatePairKey> _confirmedRelationPairs = [];
    private string _trashRoot;
    private readonly SingleTrackPreviewPlayer _previewPlayer = new();
    private Button? _playingButton;
    private string _groupTitle = "重複グループ";
    private string _fileCountText = string.Empty;
    private string _keepStateText = string.Empty;
    private DuplicateGroupTrackViewModel? _selectedTrack;

    /// <summary>
    /// 指定Libraryから見たGlobal Duplicate Group詳細画面を生成する。
    /// </summary>
    public DuplicateGroupDetailsWindow(string databasePath, long libraryId, long groupId)
        : this(databasePath, libraryId, groupId, string.Empty)
    {
    }

    /// <summary>
    /// 指定Libraryから見たGlobal Duplicate Group詳細画面を生成する。
    /// </summary>
    /// <param name="databasePath">TrackMatchのSQLiteデータベースパス</param>
    /// <param name="libraryId">現在画面で選択しているLibrary</param>
    /// <param name="groupId">表示対象のGlobal Duplicate Group</param>
    /// <param name="trashRoot">App-wide Trashのルート。未指定時は保存済みApp設定から読み込む</param>
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
            RefreshSelectedRelations();
        }
    }

    public ObservableCollection<DuplicateGroupTrackViewModel> AllTracks { get; } = [];
    public ObservableCollection<DuplicateGroupRelationViewModel> SelectedRelations { get; } = [];

    /// <summary>選択中のファイルに直接つながる確定済み重複がない場合だけ表示する案内。</summary>
    public string RelationEmptyText => SelectedRelations.Count == 0
        ? "このファイルと直接結び付く、確認済みの重複判定はありません。"
        : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void DuplicateGroupDetailsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DuplicateGroupDetailsWindow_Loaded;
        try
        {
            if (string.IsNullOrWhiteSpace(_trashRoot))
            {
                // Main Windowと同じ永続設定を読むことで、既存の3引数呼び出しを維持したままTrash設定を共有する。
                var settings = await new JsonAppSettingsStore().LoadAsync();
                _trashRoot = settings.TrashRoot ?? string.Empty;
            }

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
            throw new InvalidOperationException("指定された重複グループは現在のライブラリから参照できません。");
        }

        var trackLookup = new SqliteTrackLookupRepository(database);
        var localTrackIds = group.TrackIds.ToHashSet();
        var trackModels = new Dictionary<long, DuplicateGroupTrackViewModel>();
        foreach (var trackId in group.GlobalTrackIds)
        {
            var track = await trackLookup.GetByIdAsync(trackId)
                ?? throw new InvalidDataException($"重複グループ内のファイル #{trackId} が見つかりません。");
            trackModels[trackId] = DuplicateGroupTrackViewModel.Create(
                track,
                group.KeepTrackId == trackId,
                localTrackIds.Contains(trackId));
        }

        GroupTitle = $"重複グループ #{group.Id}";
        var externalCount = group.GlobalTrackIds.Count - group.TrackIds.Count;
        FileCountText = externalCount > 0
            ? $"ファイル数: {group.GlobalTrackIds.Count}（現在のライブラリ: {group.TrackIds.Count} / 現在のライブラリ外: {externalCount}）"
            : $"ファイル数: {group.TrackIds.Count}（すべて現在のライブラリ）";
        KeepStateText = group.KeepStatus switch
        {
            DuplicateGroupKeepStatus.Selected => "このライブラリで残すファイルは確定済みです。",
            DuplicateGroupKeepStatus.Conflict => "以前の指定が競合しています。残すファイルを再確認してください。",
            DuplicateGroupKeepStatus.Missing => "以前残すよう指定したファイルが見つかりません。残すファイルを再確認してください。",
            _ => "残すファイルはまだ選択されていません。",
        };

        AllTracks.Clear();
        _confirmedRelationPairs.Clear();
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
            _confirmedRelationPairs.Add(review.Pair);
        }

        SelectedTrack = previouslySelectedTrackId is { } selectedId
            ? AllTracks.FirstOrDefault(item => item.TrackId == selectedId) ?? AllTracks.FirstOrDefault()
            : AllTracks.FirstOrDefault(item => item.IsKeep) ?? AllTracks.FirstOrDefault();

        // 再読み込み前後で同じインスタンスが選ばれるケースでも、判定一覧は最新レビューから作り直す。
        RefreshSelectedRelations();
    }

    private void RefreshSelectedRelations()
    {
        SelectedRelations.Clear();
        var selected = SelectedTrack;
        if (selected is null)
        {
            OnPropertyChanged(nameof(RelationEmptyText));
            return;
        }

        foreach (var pair in _confirmedRelationPairs)
        {
            long? counterpartId = pair.TrackIdA == selected.TrackId
                ? pair.TrackIdB
                : pair.TrackIdB == selected.TrackId
                    ? pair.TrackIdA
                    : null;
            if (counterpartId is null)
            {
                continue;
            }

            var counterpart = AllTracks.FirstOrDefault(item => item.TrackId == counterpartId.Value);
            if (counterpart is null)
            {
                continue;
            }

            SelectedRelations.Add(new DuplicateGroupRelationViewModel(
                counterpart.Title,
                counterpart.Path,
                counterpart.ScopeLabel));
        }

        OnPropertyChanged(nameof(RelationEmptyText));
    }

    private async void SetSelectedKeep_Click(object sender, RoutedEventArgs e)
    {
        var track = SelectedTrack;
        if (track is null || !track.CanSelectAsKeep)
        {
            return;
        }

        var detail = track.IsInCurrentLibrary
            ? "このライブラリの重複グループで、このファイル以外がごみ箱への移動対象になります。"
            : "このファイルは現在のライブラリ外ですが、この重複グループを構成するファイルなので残すファイルとして選択できます。現在のライブラリへ追加されることはありません。";
        var confirmation = new ConfirmationDialog(
            "残すファイルを変更",
            $"「{track.Title}」をこのライブラリで残すファイルに設定しますか？",
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
                "メイン画面のライブラリ設定からごみ箱フォルダを設定してから実行してください。",
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
                ShowError("ごみ箱処理失敗", "対象ファイルを確認できませんでした", track.Path);
                return;
            }

            var impact = preview.SharedTrackImpacts.SingleOrDefault();
            var detail = "このファイルは現在のライブラリには所属していません。元の物理ファイルをごみ箱へ移動するため、他のライブラリから同じファイルを参照している場合も影響します。";
            if (impact is not null && impact.OtherLibraries.Count > 0)
            {
                detail += $"\n\n参照中の他のライブラリ: {string.Join("、", impact.OtherLibraries.Select(item => item.Name))}";
            }
            if (impact is not null && impact.KeepLibraries.Count > 0)
            {
                detail += $"\n\n警告: {string.Join("、", impact.KeepLibraries.Select(item => item.Name))} ではこのファイルが「残すファイル」に指定されています。移動後は残すファイルを再確認する必要があります。";
            }

            var confirmation = new ConfirmationDialog(
                "現在のライブラリ外のファイルをごみ箱へ移動",
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
                    "別名で移動するか、このファイルの移動を中止するかを選択してください。",
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
                ShowError("ごみ箱へ移動できません", previewItem.Message ?? "このファイルは現在移動できません。", track.Path);
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
            ShowError("ごみ箱処理失敗", "現在のライブラリ外にあるファイルのごみ箱処理を完了できませんでした", exception.Message);
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

/// <summary>
/// 選択中のファイルから見た、重複として確認済みの相手ファイルを表示するモデル。
/// </summary>
public sealed record DuplicateGroupRelationViewModel(
    string Title,
    string Path,
    string ScopeLabel);
