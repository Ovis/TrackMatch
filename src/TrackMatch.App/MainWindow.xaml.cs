using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
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
        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        PlaybackSeekSlider.PreviewMouseLeftButtonDown += PlaybackSeekSlider_PreviewMouseLeftButtonDown;
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(MainWindow_PreviewMouseUp), handledEventsToo: true);
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        // WPF Sliderの既定動作はトラッククリック時にLargeChange分だけ移動する。
        // 再生位置と音量はクリック位置へ直接移動する方が操作意図に一致するため、この画面のSliderへ統一して適用する。
        EnableMoveToPointForSliders(this);

        _playbackTimer.Start();
        await _viewModel.LoadAsync();

        // 初回利用でLibraryが無い場合だけ作成Dialogを自動表示する。Cancel時は空のMain Windowをそのまま利用できる。
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
        // Slider操作中に再生位置を50ms周期で上書きするとThumbが再生位置へ引き戻されるため、
        // ポインター操作が終わるまでは表示更新を止め、MouseUpで確定した位置から再開する。
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
            _viewModel.SelectedLibrary?.Id)
        {
            Owner = this,
        };
        dialog.ShowDialog();
        await _viewModel.LoadAsync(dialog.SelectedLibraryId);
    }

    private async void AnalyzeLibrary_Click(object sender, RoutedEventArgs e)
        => await _viewModel.AnalyzeLibraryAsync();

    private void CancelAnalysis_Click(object sender, RoutedEventArgs e)
        => _viewModel.CancelAnalysis();

    private void ShowAnalysisErrors_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.AnalysisErrors.Count == 0)
        {
            return;
        }

        new ScanErrorDialog(_viewModel.AnalysisErrors) { Owner = this }.ShowDialog();
    }

    private async void BrowseTrashRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await PickAndSetTrashRootAsync();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Trash Root設定失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void PreviewTrash_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureTrashRootAsync())
            {
                return;
            }

            var result = await _viewModel.ProcessTrashAsync(execute: false);
            if (result is null)
            {
                return;
            }

            var collisions = result.Items.Count(item => item.Status == RejectedTrackMoveStatus.DestinationExists);
            MessageBox.Show(
                this,
                $"移動可能: {result.ReadyCount}件\nCollision: {collisions}件\nBlocked: {result.BlockedCount}件\n\n実際のファイル移動はまだ行っていません。",
                "Trash Dry-run",
                MessageBoxButton.OK,
                result.BlockedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Trash処理失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

            var collisionBehavior = TrashDestinationCollisionBehavior.Skip;
            var collisions = preview.Items.Count(item => item.Status == RejectedTrackMoveStatus.DestinationExists);
            if (collisions > 0)
            {
                var collisionChoice = MessageBox.Show(
                    this,
                    $"Trash側に同じPathのファイルが {collisions} 件あります。\n\nはい: Track (2).flac のような最小Available番号で別名移動\nいいえ: Collisionしたファイルをスキップ\nキャンセル: Trash処理を中止",
                    "Destination Collision",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);
                if (collisionChoice == MessageBoxResult.Cancel)
                {
                    return;
                }

                collisionBehavior = collisionChoice == MessageBoxResult.Yes
                    ? TrashDestinationCollisionBehavior.Rename
                    : TrashDestinationCollisionBehavior.Skip;
            }

            var confirmation = MessageBox.Show(
                this,
                "ConfirmedDuplicateで破棄対象にしたファイルをTrashへ移動します。\n元ファイルの場所から実際に移動されます。続行しますか？",
                "Trashへ移動",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var result = await _viewModel.ProcessTrashAsync(execute: true, collisionBehavior);
            if (result is null)
            {
                return;
            }

            MessageBox.Show(
                this,
                $"移動完了: {result.MovedCount}件\nBlocked / Skipped: {result.BlockedCount}件",
                "Trash移動結果",
                MessageBoxButton.OK,
                result.BlockedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Trash処理失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Trash Root未設定時だけFolder Pickerを表示し、設定後は呼出元のTrash操作をそのまま続行できるようにする。
    /// </summary>
    private async Task<bool> EnsureTrashRootAsync()
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.TrashRoot))
        {
            return true;
        }

        return await PickAndSetTrashRootAsync();
    }

    private async Task<bool> PickAndSetTrashRootAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Trashのルートフォルダを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        await _viewModel.SetTrashRootAsync(dialog.FolderName);
        return true;
    }

    private void PlaybackPlayPause_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.TogglePlayPause();

    private void PlaybackStop_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.Stop();

    private void PlaybackSeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _isPlaybackSeekPointerActive = true;

    private void PlaybackSeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.Playback.SeekSeconds(PlaybackSeekSlider.Value);
        _isPlaybackSeekPointerActive = false;
    }

    private void MainWindow_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !PlaybackSeekSlider.IsMouseCaptureWithin)
        {
            // Slider外でボタンを離した場合にも更新抑止状態を残さない。
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

    private void PlaybackPositionTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.Playback.CommitPositionText(PlaybackPositionTextBox.Text);
            e.Handled = true;
        }
    }

    private void PlaybackPositionTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _viewModel.Playback.CommitPositionText(PlaybackPositionTextBox.Text);

    private void OffsetAMinus_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.AdjustOffset(isTrackA: true, -OffsetStep);

    private void OffsetAPlus_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.AdjustOffset(isTrackA: true, OffsetStep);

    private void OffsetBMinus_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.AdjustOffset(isTrackA: false, -OffsetStep);

    private void OffsetBPlus_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.AdjustOffset(isTrackA: false, OffsetStep);

    private void OffsetATextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.Playback.CommitOffsetText(isTrackA: true, OffsetATextBox.Text);
            e.Handled = true;
        }
    }

    private void OffsetATextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _viewModel.Playback.CommitOffsetText(isTrackA: true, OffsetATextBox.Text);

    private void OffsetBTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.Playback.CommitOffsetText(isTrackA: false, OffsetBTextBox.Text);
            e.Handled = true;
        }
    }

    private void OffsetBTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _viewModel.Playback.CommitOffsetText(isTrackA: false, OffsetBTextBox.Text);

    private void ResetOffset_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.ResetOffsetToAnalysis();

    /// <summary>
    /// Main Window配下のSliderを、トラッククリック時にクリック位置へ直接移動する操作へ統一する。
    /// </summary>
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

    private async void NotDuplicate_Click(object sender, RoutedEventArgs e) => await _viewModel.MarkNotDuplicateAsync();

    private async void KeepA_Click(object sender, RoutedEventArgs e) => await _viewModel.ConfirmDuplicateKeepAAsync();

    private async void KeepB_Click(object sender, RoutedEventArgs e) => await _viewModel.ConfirmDuplicateKeepBAsync();
}