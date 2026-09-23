using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Libraries;
using TrackMatch.Core.Trash;

namespace TrackMatch.App;

/// <summary>
/// TrackMatchのMain Window。Library選択とDialog起動等のWPF固有Interactionだけを扱う。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan OffsetStep = TimeSpan.FromMilliseconds(10);
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _playbackTimer;
    private bool _isPlaybackSeekPointerActive;

    /// <summary>
    /// DIで構築されたViewModelを使用してMain Windowを生成する。
    /// </summary>
    /// <param name="viewModel">アプリケーション共通サービスを注入済みのViewModel</param>
    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        PlaybackSeekSlider.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PlaybackSeekSlider_PreviewMouseDown), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(MainWindow_PreviewMouseUp), handledEventsToo: true);
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        EnableMoveToPointForSliders(this);
        _playbackTimer.Start();
        await _viewModel.LoadAsync();
        if (_viewModel.Libraries.Count == 0)
        {
            var service = new LibraryManagementService(_viewModel.DatabasePath, _viewModel.TrashRoot);
            var dialog = new NewLibraryDialog(service) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.CreatedLibrary is not null)
            {
                await _viewModel.LoadAsync(dialog.CreatedLibrary.Id);
            }
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _playbackTimer.Stop();
        _viewModel.Dispose();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        // Slider操作中に50ms周期の再生位置更新を入れるとThumbが旧位置へ戻るため、操作完了まで更新を止める。
        if (!_isPlaybackSeekPointerActive)
        {
            _viewModel.Playback.RefreshPosition();
        }
    }

    private async void LibraryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryComboBox.SelectedItem is Library library)
        {
            await _viewModel.SelectLibraryAsync(library);
        }
    }

    private async void ManageLibraries_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LibraryManagementDialog(
            new LibraryManagementService(_viewModel.DatabasePath, _viewModel.TrashRoot),
            _viewModel.SelectedLibrary?.Id,
            _viewModel.TrashRoot)
        { Owner = this };
        dialog.ShowDialog();
        if (!string.Equals(dialog.SelectedTrashRoot, _viewModel.TrashRoot, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(dialog.SelectedTrashRoot))
        {
            try
            {
                await _viewModel.SetTrashRootAsync(dialog.SelectedTrashRoot);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                new ConfirmationDialog(
                    "ごみ箱フォルダ設定失敗",
                    "ごみ箱フォルダを設定できませんでした",
                    exception.Message,
                    "閉じる",
                    kind: AppDialogKind.Error)
                { Owner = this }.ShowDialog();
            }
        }
        await _viewModel.LoadAsync(dialog.SelectedLibraryId);
    }

    private async void AnalyzeLibrary_Click(object sender, RoutedEventArgs e) => await _viewModel.AnalyzeLibraryAsync();
    private void CancelAnalysis_Click(object sender, RoutedEventArgs e) => _viewModel.CancelAnalysis();

    private void ShowContentChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ContentChanges.Count == 0)
        {
            return;
        }

        var invalidatedReviewCount = _viewModel.ContentChanges.Sum(item => item.InvalidatedReviewCount);
        var detail = string.Join(
            Environment.NewLine,
            _viewModel.ContentChanges.Select(item =>
                $"{Path.GetFileName(item.Path)} — レビュー判定解除 {item.InvalidatedReviewCount}件"));
        new ConfirmationDialog(
            "音声内容の変更",
            $"{_viewModel.ContentChanges.Count}ファイルの音声内容変更を検出しました / レビュー判定解除 {invalidatedReviewCount}件",
            detail,
            "閉じる",
            kind: AppDialogKind.Information)
        { Owner = this }.ShowDialog();
    }

    private void ShowAnalysisErrors_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.AnalysisErrors.Count > 0)
        {
            new ScanErrorDialog(_viewModel.AnalysisErrors) { Owner = this }.ShowDialog();
        }
    }

    private void CandidateTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CandidateTabs))
        {
            return;
        }

        _viewModel.CandidateListMode = CandidateTabs.SelectedIndex switch
        {
            1 => CandidateReviewListMode.Reviewed,
            2 => CandidateReviewListMode.ReReviewRecommended,
            3 => CandidateReviewListMode.All,
            _ => CandidateReviewListMode.Unreviewed,
        };
    }

    private async void ExecuteTrash_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureTrashRootAsync())
            {
                return;
            }

            var preview = await _viewModel.ProcessTrashAsync(execute: false);
            if (preview is null)
            {
                return;
            }

            if (preview.SharedTrackImpacts.Count > 0)
            {
                var impactedLibraries = preview.SharedTrackImpacts
                    .SelectMany(impact => impact.OtherLibraries)
                    .Select(library => library.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var keepLibraries = preview.SharedTrackImpacts
                    .SelectMany(impact => impact.KeepLibraries)
                    .Select(library => library.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var detail = $"影響するLibrary: {string.Join("、", impactedLibraries)}";
                if (keepLibraries.Length > 0)
                {
                    detail += $"\n\n次のライブラリでは移動対象が「残すファイル」として確定しています。移動後は残すファイルが未確定になるため、再確認が必要です: {string.Join("、", keepLibraries)}";
                }

                // Shared Trackは1つの物理ファイルを複数Libraryが参照するため、通常のTrash確認とは別に影響範囲を明示する。
                var sharedConfirmation = new ConfirmationDialog(
                    "他のLibraryにも影響します",
                    $"移動対象のうち {preview.SharedTrackImpacts.Count} 件は他のLibraryでも参照されています。",
                    detail,
                    "影響を確認して続行",
                    "キャンセル",
                    kind: keepLibraries.Length > 0 ? AppDialogKind.Warning : AppDialogKind.Information)
                { Owner = this };
                sharedConfirmation.ShowDialog();
                if (sharedConfirmation.SelectedResult != AppDialogResult.Primary)
                {
                    return;
                }
            }

            var collisionBehavior = TrashDestinationCollisionBehavior.Skip;
            var collisions = preview.Items.Count(item => item.Status == RejectedTrackMoveStatus.DestinationExists);
            if (collisions > 0)
            {
                var collisionDialog = new ConfirmationDialog(
                    "移動先のファイル重複",
                    $"ごみ箱側に同じパスのファイルが {collisions} 件あります",
                    "別名で移動するか、衝突したファイルだけスキップするかを選択してください。",
                    "別名で移動",
                    "スキップ",
                    "キャンセル",
                    AppDialogKind.Warning)
                { Owner = this };
                collisionDialog.ShowDialog();

                if (collisionDialog.SelectedResult is AppDialogResult.Tertiary or AppDialogResult.None)
                {
                    return;
                }

                collisionBehavior = collisionDialog.SelectedResult == AppDialogResult.Primary
                    ? TrashDestinationCollisionBehavior.Rename
                    : TrashDestinationCollisionBehavior.Skip;
            }

            var confirmation = new ConfirmationDialog(
                "ごみ箱へ移動",
                $"レビュー済みの破棄対象 {preview.ReadyCount} 件をごみ箱へ移動しますか？",
                "元ファイルの場所から実際に移動されます。",
                "ごみ箱へ移動",
                "キャンセル",
                kind: AppDialogKind.Warning)
            { Owner = this };
            confirmation.ShowDialog();
            if (confirmation.SelectedResult != AppDialogResult.Primary)
            {
                return;
            }

            var result = await _viewModel.ProcessTrashAsync(execute: true, collisionBehavior);
            if (result is not null)
            {
                new ConfirmationDialog(
                    "ごみ箱への移動結果",
                    $"移動完了: {result.MovedCount}件",
                    $"移動不可 / スキップ: {result.BlockedCount}件",
                    "閉じる",
                    kind: result.BlockedCount == 0 ? AppDialogKind.Information : AppDialogKind.Warning)
                { Owner = this }.ShowDialog();
            }
        }
        catch (Exception exception)
        {
            new ConfirmationDialog(
                "ごみ箱処理失敗",
                "ごみ箱処理を完了できませんでした",
                exception.Message,
                "閉じる",
                kind: AppDialogKind.Error)
            { Owner = this }.ShowDialog();
        }
    }

    private async Task<bool> EnsureTrashRootAsync()
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.TrashRoot))
        {
            return true;
        }

        var dialog = new OpenFolderDialog { Title = "ごみ箱フォルダを選択", Multiselect = false };
        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        await _viewModel.SetTrashRootAsync(dialog.FolderName);
        return true;
    }

    private void PlaybackPlayPause_Click(object sender, RoutedEventArgs e) => _viewModel.Playback.TogglePlayPause();
    private void PlaybackStop_Click(object sender, RoutedEventArgs e) => _viewModel.Playback.Stop();

    private void PlaybackSeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _isPlaybackSeekPointerActive = true;
        if (IsWithinThumb(e.OriginalSource as DependencyObject))
        {
            return;
        }
        // IsMoveToPointEnabledがValueを確定した後、Backgroundの位置更新より先にSeekする。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_isPlaybackSeekPointerActive)
            {
                _viewModel.Playback.SeekSeconds(PlaybackSeekSlider.Value);
            }
        });
    }

    private void PlaybackSeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.Playback.SeekSeconds(PlaybackSeekSlider.Value);
        _isPlaybackSeekPointerActive = false;
    }

    private void MainWindow_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !PlaybackSeekSlider.IsMouseOver && !PlaybackSeekSlider.IsMouseCaptureWithin)
        {
            _isPlaybackSeekPointerActive = false;
        }
    }

    private void PlaybackSeekSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            _viewModel.Playback.SeekSeconds(PlaybackSeekSlider.Value);
        }
    }

    private void RelativeOffsetMinus_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.AdjustOffset(isTrackA: false, -OffsetStep);

    private void RelativeOffsetPlus_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.AdjustOffset(isTrackA: false, OffsetStep);

    private void ResetOffset_Click(object sender, RoutedEventArgs e) => _viewModel.Playback.ResetOffsetToAnalysis();

    private static void EnableMoveToPointForSliders(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Slider slider)
            {
                slider.IsMoveToPointEnabled = true;
            }

            EnableMoveToPointForSliders(child);
        }
    }

    private static bool IsWithinThumb(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Thumb)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 候補行を右クリックした時点でその行を選択し、表示中の詳細とコンテキストメニューの操作対象を一致させる。
    /// </summary>
    private void CandidateGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null)
        {
            return;
        }

        CandidateGrid.SelectedItem = row.Item;
        row.Focus();
    }

    /// <summary>
    /// 指定したVisual要素から親方向へ探索し、最初に見つかった指定型の要素を返す。
    /// </summary>
    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T target)
            {
                return target;
            }
        }

        return null;
    }

    private async void NotDuplicate_Click(object sender, RoutedEventArgs e)
        => await _viewModel.ExecuteReviewWithUndoAsync(
            () => ExecuteReviewActionAsync(CandidateReviewDecision.NotDuplicate, _viewModel.MarkNotDuplicateAsync));

    private async void KeepA_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedCandidate;
        if (selected is null)
        {
            return;
        }

        await _viewModel.ExecuteReviewWithUndoAsync(
            () => ConfirmDuplicateWithImpactAsync(selected.TrackIdA, _viewModel.ConfirmDuplicateKeepAAsync));
    }

    private async void KeepB_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedCandidate;
        if (selected is null)
        {
            return;
        }

        await _viewModel.ExecuteReviewWithUndoAsync(
            () => ConfirmDuplicateWithImpactAsync(selected.TrackIdB, _viewModel.ConfirmDuplicateKeepBAsync));
    }

    private async Task ConfirmDuplicateWithImpactAsync(long preferredTrackId, Func<Task> action)
    {
        try
        {
            var impact = await _viewModel.GetKeepChangeImpactAsync(preferredTrackId);
            if (!string.IsNullOrWhiteSpace(impact))
            {
                var confirmation = new ConfirmationDialog(
                    "重複判定による「残すファイル」の変更を確認",
                    impact,
                    "優先するファイルの判定は全ライブラリで共有され、その判定から残すファイルとごみ箱への移動対象が再計算されます。",
                    "判定を確定",
                    "キャンセル",
                    kind: AppDialogKind.Warning)
                { Owner = this };
                confirmation.ShowDialog();
                if (confirmation.SelectedResult != AppDialogResult.Primary)
                {
                    return;
                }
            }

            await ExecuteReviewActionAsync(CandidateReviewDecision.ConfirmedDuplicate, action);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ShowReviewError(exception);
        }
    }

    private async Task ExecuteReviewActionAsync(CandidateReviewDecision? targetDecision, Func<Task> action)
    {
        try
        {
            if (!ConfirmGlobalVerdictOverwrite(targetDecision))
            {
                return;
            }

            await action();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ShowReviewError(exception);
        }
    }

    /// <summary>
    /// 他Libraryで確定したGlobal Human Verdictを現在Libraryから実際に変更する場合だけ、影響範囲を明示して確認する。
    /// </summary>
    /// <param name="targetDecision">操作後のGlobal Verdict。nullは未確定へ戻す操作</param>
    private bool ConfirmGlobalVerdictOverwrite(CandidateReviewDecision? targetDecision)
    {
        var selected = _viewModel.SelectedCandidate;
        var library = _viewModel.SelectedLibrary;
        if (selected is null || library is null || selected.Row.ReviewDecision is null)
        {
            return true;
        }

        // ConfirmedDuplicateのPreferred Track変更もGlobal Human Verdictの変更である。
        // Decision種別だけが同じでも別Library由来なら警告対象から除外しない。

        var sourceLibraryId = selected.Row.ReviewSourceLibraryId;
        var sourceNameSnapshot = selected.Row.ReviewSourceLibraryName;
        if (sourceLibraryId == library.Id)
        {
            return true;
        }

        // Source Library削除後はFKがNULLになるが、Snapshotが残っていれば別Libraryで確定したVerdictである。
        // NULLだけを「出所なし」と扱うと、削除済みLibrary由来のGlobal Verdictを警告なしで変更できてしまう。
        if (sourceLibraryId is null && string.IsNullOrWhiteSpace(sourceNameSnapshot))
        {
            return true;
        }

        var sourceName = !string.IsNullOrWhiteSpace(sourceNameSnapshot)
            ? sourceNameSnapshot
            : $"Library #{sourceLibraryId}";
        var confirmation = new ConfirmationDialog(
            "他のLibraryで確定した判定を変更します",
            $"この判定は「{sourceName}」で確定されています。",
            "レビュー判定はファイルの組み合わせごとに全ライブラリで共有されるため、ここで変更すると他のライブラリから見える判定も同時に変わります。",
            "共有されている判定を変更",
            "キャンセル",
            kind: AppDialogKind.Warning)
        { Owner = this };
        confirmation.ShowDialog();
        return confirmation.SelectedResult == AppDialogResult.Primary;
    }

    private void ShowReviewError(Exception exception)
    {
        new ConfirmationDialog(
            "レビュー保存失敗",
            "レビューを保存できませんでした",
            exception.Message,
            "閉じる",
            kind: AppDialogKind.Error)
        { Owner = this }.ShowDialog();
    }

    private async void ShowDuplicateGroupDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not long groupId || _viewModel.SelectedLibrary is not { } library)
        {
            return;
        }

        // Main WindowのA/B同期再生と詳細画面の簡易試聴が同時に鳴らないよう、詳細表示前に停止する。
        _viewModel.StopPlayback();
        var details = new DuplicateGroupDetailsWindow(_viewModel.DatabasePath, library.Id, groupId) { Owner = this };
        details.ShowDialog();
        if (details.RequestedReviewPair is { } pair)
        {
            await _viewModel.NavigateToCandidateAsync(pair);
        }

        await _viewModel.RefreshDuplicateGroupsAsync();
    }

    private void CandidateMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var clearReviewItem = new MenuItem
        {
            Header = "レビュー判定を解除",
            IsEnabled = _viewModel.CanClearReview,
        };
        clearReviewItem.Click += ClearReview_Click;

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
        };
        menu.Items.Add(clearReviewItem);
        menu.IsOpen = true;
    }

    private async void ClearReview_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanClearReview)
        {
            return;
        }

        var confirmation = new ConfirmationDialog(
            "レビュー判定を解除",
            "この候補のレビュー判定を解除しますか？",
            "現在のレビュー判定を削除します。重複グループと候補状態は残っている判定から再計算されます。ごみ箱へ移動済みのファイルは自動では元に戻りません。",
            "レビュー判定を解除",
            "キャンセル",
            kind: AppDialogKind.Warning)
        { Owner = this };
        confirmation.ShowDialog();

        if (confirmation.SelectedResult == AppDialogResult.Primary)
        {
            await ExecuteReviewActionAsync(targetDecision: null, _viewModel.ClearReviewAsync);
        }
    }

    /// <summary>ジャンルFilterだけを解除し、レビュー状態など他のFilterは維持する。</summary>
    private void ClearGenreFilter_Click(object sender, RoutedEventArgs e)
        => _viewModel.ClearGenreFilter();

    /// <summary>
    /// 選択内容は操作ごとに即時反映しているため、適用ボタンではPopupを閉じて一覧の確認へ戻す。
    /// </summary>
    private void ApplyGenreFilter_Click(object sender, RoutedEventArgs e)
        => GenreFilterToggle.IsChecked = false;

}
