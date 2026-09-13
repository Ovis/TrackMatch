using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using TrackMatch.Application;
using TrackMatch.Core.Libraries;

namespace TrackMatch.App;

/// <summary>
/// Library CRUD、Root管理、レビュー済み重複音源のTrash設定を集約するModal Dialog。
/// </summary>
public partial class LibraryManagementDialog : Window
{
    private readonly LibraryManagementService _service;
    private IReadOnlyList<Library> _libraries = [];
    private Library? _selectedLibrary;
    private bool _loadingSelection;
    private bool _nameDirty;
    private bool _revertingSelection;
    private bool _allowClose;
    private bool _closingConfirmationActive;

    public LibraryManagementDialog(LibraryManagementService service, long? selectedLibraryId, string trashRoot = "")
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitialLibraryId = selectedLibraryId;
        SelectedTrashRoot = trashRoot;
        InitializeComponent();
        TrashRootTextBox.Text = SelectedTrashRoot;
        Loaded += LibraryManagementDialog_Loaded;
    }

    public long? InitialLibraryId { get; }
    public long? SelectedLibraryId => _selectedLibrary?.Id;
    public string SelectedTrashRoot { get; private set; }

    private async void LibraryManagementDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LibraryManagementDialog_Loaded;
        await ReloadAsync(InitialLibraryId);
    }

    private async Task ReloadAsync(long? preferredId)
    {
        _libraries = await _service.GetLibrariesAsync();
        LibrariesListBox.ItemsSource = _libraries;
        var selected = _libraries.FirstOrDefault(item => item.Id == preferredId) ?? _libraries.FirstOrDefault();
        _revertingSelection = true;
        LibrariesListBox.SelectedItem = selected;
        _revertingSelection = false;
        LoadSelection(selected);
    }

    private async void LibrariesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_revertingSelection) return;
        var next = LibrariesListBox.SelectedItem as Library;
        if (!await ConfirmUnsavedNameAsync())
        {
            _revertingSelection = true;
            LibrariesListBox.SelectedItem = _selectedLibrary;
            _revertingSelection = false;
            return;
        }
        LoadSelection(next);
    }

    private void LoadSelection(Library? library)
    {
        _loadingSelection = true;
        _selectedLibrary = library;
        NameTextBox.Text = library?.Name ?? string.Empty;
        RootsListBox.ItemsSource = library?.Roots;
        _nameDirty = false;
        _loadingSelection = false;
        StatusText.Text = library is null ? "Libraryがありません。" : string.Empty;
    }

    private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSelection && _selectedLibrary is not null)
            _nameDirty = !string.Equals(NameTextBox.Text, _selectedLibrary.Name, StringComparison.Ordinal);
    }

    private async void SaveName_Click(object sender, RoutedEventArgs e) => await SaveNameAsync();

    private async Task<bool> SaveNameAsync()
    {
        if (_selectedLibrary is null || !_nameDirty) return true;
        try
        {
            var id = _selectedLibrary.Id;
            await _service.RenameLibraryAsync(id, NameTextBox.Text);
            await ReloadAsync(id);
            StatusText.Text = "Library名を保存しました。";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { StatusText.Text = exception.Message; return false; }
    }

    private async Task<bool> ConfirmUnsavedNameAsync()
    {
        if (!_nameDirty) return true;

        var dialog = new ConfirmationDialog(
            "未保存のLibrary名",
            "Library名が変更されています",
            "変更を保存するか、破棄して続行するかを選択してください。",
            "保存",
            "破棄",
            "キャンセル",
            AppDialogKind.Question)
        { Owner = this };
        dialog.ShowDialog();

        if (dialog.SelectedResult == AppDialogResult.Tertiary || dialog.SelectedResult == AppDialogResult.None)
            return false;
        if (dialog.SelectedResult == AppDialogResult.Primary)
            return await SaveNameAsync();
        return true;
    }

    private async void NewLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedNameAsync()) return;
        var dialog = new NewLibraryDialog(_service) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.CreatedLibrary is not null) await ReloadAsync(dialog.CreatedLibrary.Id);
    }

    private async void DeleteLibrary_Click(object sender, RoutedEventArgs e)
    {
        var library = _selectedLibrary;
        if (library is null) return;
        var summary = await _service.GetDeleteSummaryAsync(library.Id);

        var confirmation = new ConfirmationDialog(
            "Libraryを削除",
            $"Library '{library.Name}' を削除しますか？",
            $"Root: {summary.RootCount:N0}\nTrack: {summary.TrackCount:N0}\n\nFingerprint・Candidate・Review等のTrackMatch管理データも削除されます。元Audio Fileは削除されません。",
            "削除",
            "キャンセル",
            kind: AppDialogKind.Warning)
        { Owner = this };
        confirmation.ShowDialog();
        if (confirmation.SelectedResult != AppDialogResult.Primary) return;

        await _service.DeleteLibraryAsync(library.Id);
        await ReloadAsync(null);
    }

    private async void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var library = _selectedLibrary;
        if (library is null) return;
        var dialog = new OpenFolderDialog { Title = "追加するLibrary Rootを選択", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try { await _service.AddRootAsync(library.Id, dialog.FolderName); await ReloadAsync(library.Id); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { StatusText.Text = exception.Message; }
    }

    private async void DeleteRoot_Click(object sender, RoutedEventArgs e)
    {
        var library = _selectedLibrary;
        var root = RootsListBox.SelectedItem as LibraryRoot;
        if (library is null || root is null) return;
        try
        {
            var count = await _service.GetRootTrackCountAsync(root.Id);
            var confirmation = new ConfirmationDialog(
                "Rootを削除",
                $"Root '{root.Path}' を削除しますか？",
                $"対象Track: {count:N0}\nTrackMatch管理データは削除されますが、元Audio Fileは削除されません。",
                "削除",
                "キャンセル",
                kind: AppDialogKind.Warning)
            { Owner = this };
            confirmation.ShowDialog();
            if (confirmation.SelectedResult != AppDialogResult.Primary) return;

            await _service.RemoveRootAsync(library.Id, root.Id);
            await ReloadAsync(library.Id);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { StatusText.Text = exception.Message; }
    }

    private async void RemapRoot_Click(object sender, RoutedEventArgs e)
    {
        var library = _selectedLibrary;
        var root = RootsListBox.SelectedItem as LibraryRoot;
        if (library is null || root is null) return;
        var picker = new OpenFolderDialog { Title = "新しいRoot保存場所を選択", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var preview = await _service.PreviewRootRemapAsync(library.Id, root.Id, picker.FolderName);
            var confirmation = new RootRemapPreviewDialog(preview) { Owner = this };
            if (confirmation.ShowDialog() != true) return;
            await _service.RemapRootAsync(library.Id, root.Id, picker.FolderName);
            await ReloadAsync(library.Id);
            StatusText.Text = "Root保存場所を変更しました。通常のスキャン・分析は自動実行していません。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { StatusText.Text = exception.Message; }
    }

    private void BrowseTrashRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Trashのルートフォルダを選択", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        SelectedTrashRoot = picker.FolderName;
        TrashRootTextBox.Text = SelectedTrashRoot;
        StatusText.Text = "ごみ箱のパスは閉じるときに保存されます。";
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedNameAsync()) return;
        _allowClose = true;
        DialogResult = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || !_nameDirty) { base.OnClosing(e); return; }
        // Closingは同期イベントなので一度Cancelし、非同期保存確認が完了してから改めてCloseする。
        e.Cancel = true;
        base.OnClosing(e);
        if (!_closingConfirmationActive) { _closingConfirmationActive = true; _ = ConfirmAndCloseAsync(); }
    }

    private async Task ConfirmAndCloseAsync()
    {
        try
        {
            if (!await ConfirmUnsavedNameAsync()) return;
            _allowClose = true;
            Close();
        }
        finally { _closingConfirmationActive = false; }
    }
}
