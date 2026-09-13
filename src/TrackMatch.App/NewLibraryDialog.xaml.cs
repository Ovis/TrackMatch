using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using TrackMatch.Application;
using TrackMatch.Core.Libraries;

namespace TrackMatch.App;

/// <summary>
/// 名前と1つ以上の対象フォルダをまとめて入力し、ライブラリをAtomicに作成するDialog。
/// </summary>
public partial class NewLibraryDialog : Window
{
    private readonly LibraryManagementService _service;
    private readonly ObservableCollection<string> _roots = [];

    public NewLibraryDialog(LibraryManagementService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
        RootsListBox.ItemsSource = _roots;
    }

    public Library? CreatedLibrary { get; private set; }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "対象フォルダを選択", Multiselect = false };
        if (dialog.ShowDialog(this) == true && !_roots.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            _roots.Add(dialog.FolderName);
        }
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootsListBox.SelectedItem is string path)
        {
            _roots.Remove(path);
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        try
        {
            CreatedLibrary = await _service.CreateLibraryAsync(NameTextBox.Text, _roots.ToArray());
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            ErrorText.Text = exception.Message;
        }
    }
}
