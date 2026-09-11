using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using TrackMatch.App.Playback;
using TrackMatch.Application;
using TrackMatch.Core.Libraries;

namespace TrackMatch.App;

/// <summary>
/// TrackMatchのMain Window。Library選択とDialog起動等のWPF固有Interactionだけを扱う。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new(new WpfMediaPlayerTrackPlaybackService());

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
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
        => _viewModel.Dispose();

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

    private void BrowseTrashRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Trashのルートフォルダを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.TrashRoot = dialog.FolderName;
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

    private void PlayA_Click(object sender, RoutedEventArgs e) => _viewModel.PlayTrackA();

    private void StopPlayback_Click(object sender, RoutedEventArgs e) => _viewModel.StopPlayback();

    private void PlayB_Click(object sender, RoutedEventArgs e) => _viewModel.PlayTrackB();

    private async void NotDuplicate_Click(object sender, RoutedEventArgs e) => await _viewModel.MarkNotDuplicateAsync();

    private async void KeepA_Click(object sender, RoutedEventArgs e) => await _viewModel.ConfirmDuplicateKeepAAsync();

    private async void KeepB_Click(object sender, RoutedEventArgs e) => await _viewModel.ConfirmDuplicateKeepBAsync();
}
