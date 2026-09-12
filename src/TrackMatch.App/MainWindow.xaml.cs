using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TrackMatch.App.Playback;
using TrackMatch.Application;
using TrackMatch.Core.Libraries;

namespace TrackMatch.App;

/// <summary>
/// TrackMatchのMain Window。Library選択とDialog起動等のWPF固有Interactionだけを扱う。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan OffsetStep = TimeSpan.FromMilliseconds(10);
    private readonly MainWindowViewModel _viewModel = new(new NAudioSynchronizedPlaybackService());
    private readonly DispatcherTimer _playbackTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        _playbackTimer.Start();
        await _viewModel.LoadAsync();

        // 初回利用でLibraryが無い場合だけ作成Dialogを自動表示する。Cancel時は空のMain Windowをそのまま利用できる。
        if (_viewModel.Libraries.Count == 0)
        {
            var service = new LibraryManagementService(_viewModel.DatabasePath);
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
        => _viewModel.Playback.RefreshPosition();

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
            new LibraryManagementService(_viewModel.DatabasePath),
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
        var dialog = new OpenFolderDialog
        {
            Title = "Trashのルートフォルダを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SetTrashRootAsync(dialog.FolderName);
        }
    }

    private async void PreviewTrash_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _viewModel.ProcessTrashAsync(execute: false);
            if (result is null)
            {
                return;
            }

            MessageBox.Show(
                this,
                $"移動可能: {result.ReadyCount}件\nBlocked: {result.BlockedCount}件\n\n実際のファイル移動はまだ行っていません。",
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

        try
        {
            var result = await _viewModel.ProcessTrashAsync(execute: true);
            if (result is null)
            {
                return;
            }

            MessageBox.Show(
                this,
                $"移動完了: {result.MovedCount}件\nBlocked: {result.BlockedCount}件",
                "Trash移動結果",
                MessageBoxButton.OK,
                result.BlockedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Trash処理失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PlaybackPlayPause_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.TogglePlayPause();

    private void PlaybackStop_Click(object sender, RoutedEventArgs e)
        => _viewModel.Playback.Stop();

    private void PlaybackSeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _viewModel.Playback.SeekSeconds(PlaybackSeekSlider.Value);

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

    private async void NotDuplicate_Click(object sender, RoutedEventArgs e) => await _viewModel.MarkNotDuplicateAsync();

    private async void KeepA_Click(object sender, RoutedEventArgs e) => await _viewModel.ConfirmDuplicateKeepAAsync();

    private async void KeepB_Click(object sender, RoutedEventArgs e) => await _viewModel.ConfirmDuplicateKeepBAsync();
}
