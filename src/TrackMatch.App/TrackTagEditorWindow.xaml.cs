using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TrackMatch.Application;
using TrackMatch.Core.Models;

namespace TrackMatch.App;

/// <summary>選択中のA/B音源の曲情報を並べて編集するDialog。</summary>
public partial class TrackTagEditorWindow : Window
{
    private readonly TrackTagEditingService _service;
    private readonly long _trackIdA;
    private readonly long _trackIdB;
    private readonly string _pathA;
    private readonly string _pathB;
    private readonly List<TagFieldRow> _rows = [];
    private AudioTrackMetadata? _originalA;
    private AudioTrackMetadata? _originalB;
    private bool _isBusy;
    private bool _allowClose;

    public TrackTagEditorWindow(TrackTagEditingService service, CandidateReviewItemViewModel candidate)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        ArgumentNullException.ThrowIfNull(candidate);
        _trackIdA = candidate.TrackIdA;
        _trackIdB = candidate.TrackIdB;
        _pathA = candidate.Row.PathA;
        _pathB = candidate.Row.PathB;
        InitializeComponent();
        FileNameA.Text = Path.GetFileName(_pathA);
        FileNameB.Text = Path.GetFileName(_pathB);
        FileNameA.ToolTip = _pathA;
        FileNameB.ToolTip = _pathB;
        Loaded += Window_Loaded;
    }

    public bool SavedAny { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        SetBusy(true);
        StatusText.Text = "曲情報を読み込んでいます...";
        try
        {
            var a = Task.Run(() => _service.Read(_pathA));
            var b = Task.Run(() => _service.Read(_pathB));
            await Task.WhenAll(a, b);
            _originalA = a.Result;
            _originalB = b.Result;
            CreateRows();
            StatusText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"曲情報を読み込めませんでした: {exception.Message}";
            RowsItems.IsEnabled = false;
            SaveButton.IsEnabled = false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CreateRows()
    {
        _rows.Clear();
        _rows.Add(new TagFieldRow("title", "タイトル", _originalA!.Title ?? "", _originalB!.Title ?? ""));
        _rows.Add(new TagFieldRow("artist", "アーティスト", FormatList(_originalA.Artists), FormatList(_originalB.Artists)));
        _rows.Add(new TagFieldRow("album", "アルバム", _originalA.Album ?? "", _originalB.Album ?? ""));
        _rows.Add(new TagFieldRow("genre", "ジャンル", FormatList(_originalA.Genres), FormatList(_originalB.Genres)));
        _rows.Add(new TagFieldRow("year", "年", FormatNumber(_originalA.Year), FormatNumber(_originalB.Year)));
        _rows.Add(new TagFieldRow("track", "曲番号", FormatNumber(_originalA.TrackNumber), FormatNumber(_originalB.TrackNumber)));
        _rows.Add(new TagFieldRow("disc", "ディスク番号", FormatNumber(_originalA.DiscNumber), FormatNumber(_originalB.DiscNumber)));
        RowsItems.ItemsSource = _rows;
    }

    private void CopyBToA_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TagFieldRow row }) row.ValueA = row.ValueB;
    }

    private void CopyAToB_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TagFieldRow row }) row.ValueB = row.ValueA;
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveChangesAsync(closeOnSuccess: true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_isBusy)
        {
            e.Cancel = true;
            return;
        }

        if (!HasUnsavedChanges()) return;
        e.Cancel = true;
        var confirmation = new ConfirmationDialog(
            "未保存の曲情報",
            "曲情報が変更されています",
            "変更を保存するか、破棄して閉じるかを選択してください。",
            "保存", "破棄", "編集に戻る", AppDialogKind.Question)
        { Owner = this };
        confirmation.ShowDialog();
        if (confirmation.SelectedResult == AppDialogResult.Primary)
        {
            await SaveChangesAsync(closeOnSuccess: true);
        }
        else if (confirmation.SelectedResult == AppDialogResult.Secondary)
        {
            _allowClose = true;
            // Closingイベント中の再入CloseはWPFに無視されるため、元のCloseが戻ってから閉じる。
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private async Task SaveChangesAsync(bool closeOnSuccess)
    {
        if (_isBusy || _originalA is null || _originalB is null) return;
        AudioTrackMetadata editedA;
        AudioTrackMetadata editedB;
        try
        {
            // 両側を先に検証し、Bの入力エラーでAだけ保存されることを避ける。
            editedA = BuildEdited(_originalA, sideA: true);
            editedB = BuildEdited(_originalB, sideA: false);
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = exception.Message;
            return;
        }

        SetBusy(true);
        var savingSide = "A";
        try
        {
            if (IsSideDirty(sideA: true))
            {
                StatusText.Text = "音源 A のタグを保存しています...";
                _originalA = await Task.Run(() => _service.SaveAsync(_trackIdA, _originalA, editedA));
                SyncRows(sideA: true, _originalA);
                SavedAny = true;
            }

            if (IsSideDirty(sideA: false))
            {
                savingSide = "B";
                StatusText.Text = "音源 B のタグを保存しています...";
                _originalB = await Task.Run(() => _service.SaveAsync(_trackIdB, _originalB, editedB));
                SyncRows(sideA: false, _originalB);
                SavedAny = true;
            }

            StatusText.Text = SavedAny ? "曲情報を保存しました。" : "変更はありません。";
            if (closeOnSuccess)
            {
                _allowClose = true;
                Close();
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"音源 {savingSide} のタグを保存できませんでした: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool HasUnsavedChanges() => IsSideDirty(sideA: true) || IsSideDirty(sideA: false);

    private bool IsSideDirty(bool sideA)
    {
        var original = sideA ? _originalA : _originalB;
        if (original is null) return false;
        return _rows.Any(row => (sideA ? row.ValueA : row.ValueB) != FormatValue(row.Key, original));
    }

    private AudioTrackMetadata BuildEdited(AudioTrackMetadata original, bool sideA)
    {
        string Value(string key) => sideA
            ? _rows.Single(row => row.Key == key).ValueA
            : _rows.Single(row => row.Key == key).ValueB;
        var sourceName = sideA ? "A" : "B";
        return original with
        {
            Title = NullIfWhiteSpace(Value("title")),
            Artists = ResolveList("artist", sideA),
            Album = NullIfWhiteSpace(Value("album")),
            Genres = ResolveList("genre", sideA),
            Year = ParseNumber(Value("year"), $"音源 {sourceName} の年"),
            TrackNumber = ParseNumber(Value("track"), $"音源 {sourceName} の曲番号"),
            DiscNumber = ParseNumber(Value("disc"), $"音源 {sourceName} のディスク番号"),
        };
    }

    private IReadOnlyList<string> ResolveList(string key, bool sideA)
    {
        var row = _rows.Single(item => item.Key == key);
        var value = sideA ? row.ValueA : row.ValueB;
        var original = sideA ? _originalA! : _originalB!;
        var ownValues = key == "artist" ? original.Artists : original.Genres;
        if (value == FormatList(ownValues)) return ownValues;

        // 未編集の反対側からコピーした値は配列の境界も保持する。
        var other = sideA ? _originalB! : _originalA!;
        var otherValues = key == "artist" ? other.Artists : other.Genres;
        var otherText = sideA ? row.ValueB : row.ValueA;
        return value == otherText && otherText == FormatList(otherValues)
            ? otherValues
            : ParseList(value);
    }

    private void SyncRows(bool sideA, AudioTrackMetadata metadata)
    {
        foreach (var row in _rows)
        {
            if (sideA) row.ValueA = FormatValue(row.Key, metadata);
            else row.ValueB = FormatValue(row.Key, metadata);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        var ready = !busy && _originalA is not null && _originalB is not null;
        RowsItems.IsEnabled = ready;
        SaveButton.IsEnabled = ready;
        CancelButton.IsEnabled = !busy;
    }

    private static string FormatValue(string key, AudioTrackMetadata metadata) => key switch
    {
        "title" => metadata.Title ?? "",
        "artist" => FormatList(metadata.Artists),
        "album" => metadata.Album ?? "",
        "genre" => FormatList(metadata.Genres),
        "year" => FormatNumber(metadata.Year),
        "track" => FormatNumber(metadata.TrackNumber),
        "disc" => FormatNumber(metadata.DiscNumber),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private static string FormatList(IReadOnlyList<string> values) => string.Join("; ", values);
    private static IReadOnlyList<string> ParseList(string value) => value
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string FormatNumber(uint? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static uint? ParseNumber(string value, string fieldName, uint max = uint.MaxValue)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        if (!uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result == 0 || result > max)
        {
            throw new ArgumentException($"{fieldName} は1から{max}までの整数で入力してください。");
        }

        return result;
    }

    private sealed class TagFieldRow(string key, string label, string valueA, string valueB) : INotifyPropertyChanged
    {
        private string _valueA = valueA;
        private string _valueB = valueB;
        public string Key { get; } = key;
        public string Label { get; } = label;
        public string ValueA { get => _valueA; set => Set(ref _valueA, value); }
        public string ValueB { get => _valueB; set => Set(ref _valueB, value); }
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set(ref string field, string value, [CallerMemberName] string? name = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
