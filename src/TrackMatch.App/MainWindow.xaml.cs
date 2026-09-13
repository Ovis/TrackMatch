using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TrackMatch.App.Playback;
using TrackMatch.Application;
using TrackMatch.Core.Libraries;
using TrackMatch.Core.Trash;

namespace TrackMatch.App;

/// <summary>
/// TrackMatchのMain Window。Library選択とDialog起動等のWPF固有Interactionだけを扱う。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan OffsetStep = TimeSpan.FromMilliseconds(10);
    private readonly MainWindowViewModel _viewModel = new(new NAudioSynchronizedPlaybackService());
    private readonly DispatcherTimer _playbackTimer;
    private bool _isPlaybackSeekPointerActive;

    public MainWindow()
    {
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
                await _viewModel.LoadAsync(dialog.CreatedLibrary.Id);
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
        if (!_isPlaybackSeekPointerActive) _viewModel.Playback.RefreshPosition();
    }

    private async void LibraryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryComboBox.SelectedItem is Library library) await _viewModel.SelectLibraryAsync(library);
    }

    private async void ManageLibraries_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LibraryManagementDialog(
            new LibraryManagementService(_viewModel.DatabasePath, _viewModel.TrashRoot),
            _viewModel.SelectedLibrary?.Id,
            _viewModel.TrashRoot) { Owner = this };
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

    private void ShowAnalysisErrors_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.AnalysisErrors.Count > 0) new ScanErrorDialog(_viewModel.AnalysisErrors) { Owner = this }.ShowDialog();
    }

    private void CandidateTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CandidateTabs)) return;
        _viewModel.CandidateListMode = CandidateTabs.SelectedIndex switch
        {
            1 => CandidateReviewListMode.Reviewed,
            2 => CandidateReviewListMode.All,
            _ => CandidateReviewListMode.Unreviewed,
        };
    }

    private async void ExecuteTrash_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureTrashRootAsync()) return;
            var preview = await _viewModel.ProcessTrashAsync(execute: false);
            if (preview is null) return;

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
                    return;

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
            if (confirmation.SelectedResult != AppDialogResult.Primary) return;

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
        if (!string.IsNullOrWhiteSpace(_viewModel.TrashRoot)) return true;
        var dialog = new OpenFolderDialog { Title = "ごみ箱フォルダを選択", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return false;
        await _viewModel.SetTrashRootAsync(dialog.FolderName);
        return true;
    }

    private void PlaybackPlayPause_Click(object sender, RoutedEventArgs e) => _viewModel.Playback.TogglePlayPause();
    private void PlaybackStop_Click(object sender, RoutedEventArgs e) => _viewModel.Playback.Stop();

    private void PlaybackSeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _isPlaybackSeekPointerActive = true;
        if (IsWithinThumb(e.OriginalSource as DependencyObject)) return;
        // IsMoveToPointEnabledがValueを確定した後、Backgroundの位置更新より先にSeekする。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_isPlaybackSeekPointerActive) _viewModel.Playback.SeekSeconds(PlaybackSeekSlider.Value);
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
            _isPlaybackSeekPointerActive = false;
    }

    private void PlaybackSeekSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            _viewModel.Playback.SeekSeconds(PlaybackSeekSlider.Value);
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
            if (child is Slider slider) slider.IsMoveToPointEnabled = true;
            EnableMoveToPointForSliders(child);
        }
    }

    private static bool IsWithinThumb(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Thumb) return true;
        return false;
    }

    private async void NotDuplicate_Click(object sender, RoutedEventArgs e) => await _viewModel.MarkNotDuplicateAsync();
    private async void KeepA_Click(object sender, RoutedEventArgs e) => await _viewModel.ConfirmDuplicateKeepAAsync();
    private async void KeepB_Click(object sender, RoutedEventArgs e) => await _viewModel.ConfirmDuplicateKeepBAsync();

    private void CandidateMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        // 主要なレビュー3操作から低頻度操作を分離し、三点リーダーから必要な操作だけ提示する。
        var clearReviewItem = new MenuItem
        {
            Header = "レビューを未確定に戻す",
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
        if (!_viewModel.CanClearReview) return;

        var confirmation = new ConfirmationDialog(
            "レビューを未確定に戻す",
            "この候補を未レビューへ戻しますか？",
            "現在のレビュー結果を削除します。ごみ箱へ移動済みのファイルは自動では元に戻りません。",
            "未レビューへ戻す",
            "キャンセル",
            kind: AppDialogKind.Warning)
        { Owner = this };
        confirmation.ShowDialog();

        if (confirmation.SelectedResult == AppDialogResult.Primary)
            await _viewModel.ClearReviewAsync();
    }
}