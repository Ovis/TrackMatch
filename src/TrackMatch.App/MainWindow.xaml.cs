using Microsoft.Win32;
using System.Windows;
using TrackMatch.App.Playback;

namespace TrackMatch.App;

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
        // 通常利用ではDBファイルを意識させず、Scannerと共有する標準DBを起動時にそのまま開く。
        // 参照ボタンは既存DBを調査する場合などの明示的な切り替え手段として残す。
        Loaded -= MainWindow_Loaded;
        await _viewModel.LoadAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
        => _viewModel.Dispose();

    private async void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SQLite database (*.db;*.sqlite)|*.db;*.sqlite|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _viewModel.DatabasePath = dialog.FileName;
        await _viewModel.LoadAsync();
    }

    private void BrowseLibraryRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "ライブラリのルートフォルダを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.LibraryRoot = dialog.FolderName;
        }
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

    private async void Reload_Click(object sender, RoutedEventArgs e)
        => await _viewModel.LoadAsync();

    private void PlayA_Click(object sender, RoutedEventArgs e)
        => _viewModel.PlayTrackA();

    private void StopPlayback_Click(object sender, RoutedEventArgs e)
        => _viewModel.StopPlayback();

    private void PlayB_Click(object sender, RoutedEventArgs e)
        => _viewModel.PlayTrackB();

    private async void NotDuplicate_Click(object sender, RoutedEventArgs e)
        => await _viewModel.MarkNotDuplicateAsync();

    private async void KeepA_Click(object sender, RoutedEventArgs e)
        => await _viewModel.ConfirmDuplicateKeepAAsync();

    private async void KeepB_Click(object sender, RoutedEventArgs e)
        => await _viewModel.ConfirmDuplicateKeepBAsync();
}
