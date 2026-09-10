using Microsoft.Win32;
using System.Windows;

namespace TrackMatch.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

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

    private async void NotDuplicate_Click(object sender, RoutedEventArgs e)
        => await _viewModel.MarkNotDuplicateAsync();

    private async void KeepA_Click(object sender, RoutedEventArgs e)
        => await _viewModel.ConfirmDuplicateKeepAAsync();

    private async void KeepB_Click(object sender, RoutedEventArgs e)
        => await _viewModel.ConfirmDuplicateKeepBAsync();
}
