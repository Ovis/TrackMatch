using System.Windows;
using System.Windows.Controls;
using TrackMatch.Application;
using TrackMatch.Core.Libraries;
using TrackMatch.Core.Management;

namespace TrackMatch.App;

/// <summary>
/// Missing・未所属を含むGlobal Trackを確認し、Force ReanalysisとTrackMatch管理データ削除を行う画面。
/// </summary>
public partial class TrackManagementDialog : Window
{
    private readonly TrackManagementService _service;
    private readonly IReadOnlyList<Library> _libraries;
    private readonly long? _initialLibraryId;
    private IReadOnlyList<ManagedTrack> _tracks = [];
    private bool _loaded;

    /// <summary>
    /// Global Track管理画面を生成する。
    /// </summary>
    /// <param name="service">Global Track管理操作を提供するService</param>
    /// <param name="libraries">Root/Library単位のForce Reanalysis選択肢</param>
    /// <param name="initialLibraryId">初期選択するLibrary ID</param>
    public TrackManagementDialog(
        TrackManagementService service,
        IReadOnlyList<Library> libraries,
        long? initialLibraryId)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _initialLibraryId = initialLibraryId;
        InitializeComponent();
        Loaded += TrackManagementDialog_Loaded;
    }

    private async void TrackManagementDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= TrackManagementDialog_Loaded;
        LibraryComboBox.ItemsSource = _libraries;
        LibraryComboBox.SelectedItem = _libraries.FirstOrDefault(library => library.Id == _initialLibraryId)
            ?? _libraries.FirstOrDefault();
        FilterComboBox.SelectedIndex = 0;
        _loaded = true;
        await ReloadAsync();
    }

    private TrackManagementFilter SelectedFilter
    {
        get
        {
            if (FilterComboBox.SelectedItem is not ComboBoxItem item
                || item.Tag is not string tag
                || !Enum.TryParse<TrackManagementFilter>(tag, out var filter))
            {
                return TrackManagementFilter.All;
            }

            return filter;
        }
    }

    private IReadOnlyList<ManagedTrack> SelectedTracks
        => TracksGrid.SelectedItems.Cast<ManagedTrack>().ToArray();

    private async Task ReloadAsync()
    {
        try
        {
            var filter = SelectedFilter;
            _tracks = await Task.Run(() => _service.GetTracksAsync(filter));
            TracksGrid.ItemsSource = _tracks;
            CountText.Text = $"{_tracks.Count:N0} 件";
            UpdateCommandState();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ShowError("音源一覧の読み込み失敗", exception.Message);
        }
    }

    private async void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded)
        {
            await ReloadAsync();
        }
    }

    private void TracksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateCommandState();

    private void LibraryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var library = LibraryComboBox.SelectedItem as Library;
        RootComboBox.ItemsSource = library?.Roots;
        RootComboBox.SelectedItem = library?.Roots.FirstOrDefault();
    }

    private void UpdateCommandState()
    {
        var hasSelection = TracksGrid.SelectedItems.Count > 0;
        ReanalyzeSelectedButton.IsEnabled = hasSelection;
        DeleteSelectedButton.IsEnabled = hasSelection;
        DeleteFilterButton.IsEnabled = _tracks.Count > 0
            && SelectedFilter is TrackManagementFilter.Missing or TrackManagementFilter.Unowned;
    }

    private async void ReanalyzeSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTracks;
        if (selected.Count == 0)
        {
            return;
        }

        var confirmation = new ConfirmationDialog(
            "選択した音源を再解析",
            $"選択した {selected.Count:N0} 件をForce Reanalysisしますか？",
            "Fingerprint・音質解析・候補比較などの機械解析と、対象音源に関係する現在の人間レビューを無効化します。人間レビューは履歴へ保存され、次回分析後は未レビューとして確認できます。Shared Trackの場合は他ライブラリにも同じGlobal解析の無効化が反映されます。",
            "再解析対象にする",
            "キャンセル",
            kind: AppDialogKind.Warning)
        { Owner = this };
        confirmation.ShowDialog();
        if (confirmation.SelectedResult != AppDialogResult.Primary)
        {
            return;
        }

        await ExecuteAndReloadAsync(async () =>
        {
            var result = await _service.ForceReanalysisTracksAsync(selected.Select(track => track.TrackId).ToArray());
            return $"{result.TrackCount:N0} 件を再解析対象にしました。退避したレビュー: {result.ArchivedReviewCount:N0} 件";
        });
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTracks;
        if (selected.Count == 0)
        {
            return;
        }

        await DeleteTracksWithConfirmationAsync(
            selected,
            $"選択した {selected.Count:N0} 件のTrackMatch管理データを完全削除しますか？");
    }

    private async void DeleteFilter_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFilter is TrackManagementFilter.All || _tracks.Count == 0)
        {
            return;
        }

        var filterName = SelectedFilter == TrackManagementFilter.Missing ? "Missing" : "未所属";
        await DeleteTracksWithConfirmationAsync(
            _tracks,
            $"現在表示している「{filterName}」{_tracks.Count:N0} 件のTrackMatch管理データをすべて削除しますか？");
    }

    private async Task DeleteTracksWithConfirmationAsync(
        IReadOnlyCollection<ManagedTrack> tracks,
        string message)
    {
        var confirmation = new ConfirmationDialog(
            "Global Track管理データを削除",
            message,
            "元の音源ファイルは削除しません。Membership、Fingerprint、解析結果、現在レビューと履歴、Keep参照などTrackMatch側の情報を削除します。同じPathが後日スキャンで再発見された場合は、新しいTrack IDとして登録されます。この操作は取り消せません。",
            "管理データを削除",
            "キャンセル",
            kind: AppDialogKind.Warning)
        { Owner = this };
        confirmation.ShowDialog();
        if (confirmation.SelectedResult != AppDialogResult.Primary)
        {
            return;
        }

        await ExecuteAndReloadAsync(async () =>
        {
            var deleted = await _service.DeleteTracksAsync(tracks.Select(track => track.TrackId).ToArray());
            return $"{deleted:N0} 件の管理データを削除しました。";
        });
    }

    private async void ReanalyzeLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryComboBox.SelectedItem is not Library library)
        {
            return;
        }

        if (!ConfirmScopeReanalysis($"ライブラリ「{library.Name}」に所属する音源をForce Reanalysisしますか？"))
        {
            return;
        }

        await ExecuteAndReloadAsync(async () =>
        {
            var result = await _service.ForceReanalysisLibraryAsync(library.Id);
            return $"{result.TrackCount:N0} 件を再解析対象にしました。退避したレビュー: {result.ArchivedReviewCount:N0} 件";
        });
    }

    private async void ReanalyzeRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootComboBox.SelectedItem is not LibraryRoot root)
        {
            return;
        }

        if (!ConfirmScopeReanalysis($"対象フォルダ「{root.Path}」由来の音源をForce Reanalysisしますか？"))
        {
            return;
        }

        await ExecuteAndReloadAsync(async () =>
        {
            var result = await _service.ForceReanalysisRootAsync(root.Id);
            return $"{result.TrackCount:N0} 件を再解析対象にしました。退避したレビュー: {result.ArchivedReviewCount:N0} 件";
        });
    }

    private bool ConfirmScopeReanalysis(string message)
    {
        var confirmation = new ConfirmationDialog(
            "範囲を指定して再解析",
            message,
            "対象Global Trackの機械解析と現在レビューを無効化します。Shared Trackは他ライブラリでも同じGlobal解析を共有しているため、影響は選択したライブラリだけには限定されません。",
            "再解析対象にする",
            "キャンセル",
            kind: AppDialogKind.Warning)
        { Owner = this };
        confirmation.ShowDialog();
        return confirmation.SelectedResult == AppDialogResult.Primary;
    }

    private async Task ExecuteAndReloadAsync(Func<Task<string>> action)
    {
        try
        {
            IsEnabled = false;
            // Track管理操作は大量のGlobal Track・レビュー・派生状態を更新し得るため、UI Thread外で実行する。
            var message = await Task.Run(action);
            await ReloadAsync();
            new ConfirmationDialog(
                "音源管理",
                message,
                string.Empty,
                "閉じる",
                kind: AppDialogKind.Information)
            { Owner = this }.ShowDialog();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ShowError("音源管理操作に失敗", exception.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ShowError(string title, string message)
    {
        new ConfirmationDialog(
            title,
            message,
            string.Empty,
            "閉じる",
            kind: AppDialogKind.Error)
        { Owner = this }.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
