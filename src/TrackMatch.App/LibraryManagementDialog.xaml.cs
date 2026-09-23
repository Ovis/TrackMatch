using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
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
        _libraries = await Task.Run(() => _service.GetLibrariesAsync());
        var selected = _libraries.FirstOrDefault(item => item.Id == preferredId) ?? _libraries.FirstOrDefault();

        // ItemsSourceの差し替えではSelectionChangedが同期的に発火する。
        // 先に選択変更を抑止しないと、古い選択がnullへ変わった瞬間にLoadSelection(null)が走り、
        // 後続のReloadAsyncが読み直した対象フォルダを空表示で上書きすることがある。
        _revertingSelection = true;
        try
        {
            LibrariesListBox.ItemsSource = _libraries;
            LibrariesListBox.SelectedItem = selected;
        }
        finally
        {
            _revertingSelection = false;
        }

        LoadSelection(selected);
    }

    private async void LibrariesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_revertingSelection)
        {
            return;
        }

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
        StatusText.Text = library is null ? "ライブラリがありません。" : string.Empty;
    }

    private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSelection && _selectedLibrary is not null)
        {
            _nameDirty = !string.Equals(NameTextBox.Text, _selectedLibrary.Name, StringComparison.Ordinal);
        }
    }

    private async void SaveName_Click(object sender, RoutedEventArgs e) => await SaveNameAsync();

    private async Task<bool> SaveNameAsync()
    {
        if (_selectedLibrary is null || !_nameDirty)
        {
            return true;
        }

        try
        {
            var id = _selectedLibrary.Id;
            var name = NameTextBox.Text;
            await Task.Run(() => _service.RenameLibraryAsync(id, name));
            await ReloadAsync(id);
            StatusText.Text = "ライブラリ名を保存しました。";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { StatusText.Text = exception.Message; return false; }
    }

    private async Task<bool> ConfirmUnsavedNameAsync()
    {
        if (!_nameDirty)
        {
            return true;
        }

        var dialog = new ConfirmationDialog(
            "未保存のライブラリ名",
            "ライブラリ名が変更されています",
            "変更を保存するか、破棄して続行するかを選択してください。",
            "保存",
            "破棄",
            "キャンセル",
            AppDialogKind.Question)
        { Owner = this };
        dialog.ShowDialog();

        if (dialog.SelectedResult == AppDialogResult.Tertiary || dialog.SelectedResult == AppDialogResult.None)
        {
            return false;
        }

        if (dialog.SelectedResult == AppDialogResult.Primary)
        {
            return await SaveNameAsync();
        }

        return true;
    }

    private async void NewLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedNameAsync())
        {
            return;
        }

        var dialog = new NewLibraryDialog(_service) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.CreatedLibrary is not null)
        {
            await ReloadAsync(dialog.CreatedLibrary.Id);
        }
    }

    private async void DeleteLibrary_Click(object sender, RoutedEventArgs e)
    {
        var library = _selectedLibrary;
        if (library is null)
        {
            return;
        }

        var summary = await Task.Run(() => _service.GetDeleteSummaryAsync(library.Id));

        var confirmation = new ConfirmationDialog(
            "ライブラリを削除",
            $"ライブラリ「{library.Name}」を削除しますか？",
            $"対象フォルダ: {summary.RootCount:N0}\n登録音源: {summary.TrackCount:N0}\n\nこのライブラリの対象フォルダ・所属情報・ライブラリ固有のKeep状態を削除します。Global Track、フィンガープリント、比較結果、人間の重複判定、元の音源ファイルは削除されません。",
            "削除",
            "キャンセル",
            kind: AppDialogKind.Warning)
        { Owner = this };
        confirmation.ShowDialog();
        if (confirmation.SelectedResult != AppDialogResult.Primary)
        {
            return;
        }

        await Task.Run(() => _service.DeleteLibraryAsync(library.Id));
        await ReloadAsync(null);
    }

    private async void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var library = _selectedLibrary;
        if (library is null)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = "追加する対象フォルダを選択", Multiselect = false };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var folderName = dialog.FolderName;
            await Task.Run(() => _service.AddRootAsync(library.Id, folderName));
            await ReloadAsync(library.Id);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { StatusText.Text = exception.Message; }
    }

    private async void DeleteRoot_Click(object sender, RoutedEventArgs e)
    {
        var library = _selectedLibrary;
        var root = RootsListBox.SelectedItem as LibraryRoot;
        if (library is null || root is null)
        {
            return;
        }

        try
        {
            var count = await Task.Run(() => _service.GetRootTrackCountAsync(root.Id));
            var confirmation = new ConfirmationDialog(
                "対象フォルダを削除",
                $"対象フォルダ「{root.Path}」を削除しますか？",
                $"この対象フォルダ由来の所属情報 {count:N0} 件をライブラリから外します。Global Track、フィンガープリント、比較結果、人間の重複判定、元の音源ファイルは削除されません。",
                "削除",
                "キャンセル",
                kind: AppDialogKind.Warning)
            { Owner = this };
            confirmation.ShowDialog();
            if (confirmation.SelectedResult != AppDialogResult.Primary)
            {
                return;
            }

            await Task.Run(() => _service.RemoveRootAsync(library.Id, root.Id));
            await ReloadAsync(library.Id);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { StatusText.Text = exception.Message; }
    }

    private async void RemapRoot_Click(object sender, RoutedEventArgs e)
    {
        var library = _selectedLibrary;
        var root = RootsListBox.SelectedItem as LibraryRoot;
        if (library is null || root is null)
        {
            return;
        }

        var picker = new OpenFolderDialog { Title = "新しい保存場所を選択", Multiselect = false };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var folderName = picker.FolderName;
            var preview = await Task.Run(() => _service.PreviewRootRemapAsync(library.Id, root.Id, folderName));
            var confirmation = new RootRemapPreviewDialog(preview) { Owner = this };
            if (confirmation.ShowDialog() != true)
            {
                return;
            }

            await Task.Run(() => _service.RemapRootAsync(library.Id, root.Id, folderName));
            await ReloadAsync(library.Id);
            StatusText.Text = "対象フォルダの保存場所を変更しました。通常のスキャン・分析は自動実行していません。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { StatusText.Text = exception.Message; }
    }

    private void BrowseTrashRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "ごみ箱フォルダを選択", Multiselect = false };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        SelectedTrashRoot = picker.FolderName;
        TrashRootTextBox.Text = SelectedTrashRoot;
        StatusText.Text = "ごみ箱のパスは閉じるときに保存されます。";
    }

    private async void ManageTracks_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedNameAsync())
        {
            return;
        }

        // Track管理はGlobal DB状態を変更するため、現在のLibrary一覧をSnapshotとして渡し、
        // Dialogを閉じた後にLibrary表示も読み直してMembership削除等を反映する。
        var selectedId = _selectedLibrary?.Id;
        new TrackManagementDialog(
            new TrackManagementService(_service.DatabasePath),
            _libraries,
            selectedId)
        { Owner = this }.ShowDialog();
        await ReloadAsync(selectedId);
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedNameAsync())
        {
            return;
        }

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
            if (!await ConfirmUnsavedNameAsync())
            {
                return;
            }

            _allowClose = true;
            Close();
        }
        finally { _closingConfirmationActive = false; }
    }
}
