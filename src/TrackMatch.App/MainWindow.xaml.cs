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
