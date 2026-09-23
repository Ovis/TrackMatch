using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TrackMatch.App;

/// <summary>
/// ジャンルFilterの結合方法を表す。
/// </summary>
public enum GenreFilterMode
{
    Or,
    And,
}

/// <summary>
/// ジャンルFilterの選択肢を保持する。
/// </summary>
public sealed class GenreFilterOptionViewModel : INotifyPropertyChanged
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    /// <summary>ジャンルFilterの選択肢を生成する。</summary>
    internal GenreFilterOptionViewModel(string key, string displayName, bool isSelected, Action selectionChanged)
    {
        Key = key;
        DisplayName = displayName;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    internal string Key { get; }
    public string DisplayName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            _selectionChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Main WindowのジャンルFilter状態と、重複候補Group単位の絞り込みを管理する。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const string MissingGenreKey = "\u0000missing-genre";
    private const string MissingGenreDisplayName = "ジャンル未設定";
    private string _genreFilterSearchText = string.Empty;
    private GenreFilterMode _selectedGenreFilterMode = GenreFilterMode.Or;

    public ObservableCollection<GenreFilterOptionViewModel> GenreOptions { get; } = [];
    public ObservableCollection<GenreFilterOptionViewModel> FilteredGenreOptions { get; } = [];

    public IReadOnlyList<GenreFilterModeItem> GenreFilterModes { get; } =
    [
        new(GenreFilterMode.Or, "OR"),
        new(GenreFilterMode.And, "AND"),
    ];

    public GenreFilterMode SelectedGenreFilterMode
    {
        get => _selectedGenreFilterMode;
        set
        {
            if (_selectedGenreFilterMode == value)
            {
                return;
            }

            _selectedGenreFilterMode = value;
            OnPropertyChanged();
            ApplyCandidateFilter();
        }
    }

    public string GenreFilterSearchText
    {
        get => _genreFilterSearchText;
        set
        {
            if (_genreFilterSearchText == value)
            {
                return;
            }

            _genreFilterSearchText = value ?? string.Empty;
            OnPropertyChanged();
            RefreshFilteredGenreOptions();
        }
    }

    public bool HasGenreFilter => GenreOptions.Any(option => option.IsSelected);

    public string GenreFilterSummary
    {
        get
        {
            var selected = GenreOptions.Where(option => option.IsSelected).ToArray();
            return selected.Length switch
            {
                0 => "ジャンルを選択...",
                1 => selected[0].DisplayName,
                2 => string.Join(", ", selected.Select(option => option.DisplayName)),
                _ => $"{selected[0].DisplayName}, {selected[1].DisplayName} (+{selected.Length - 2})",
            };
        }
    }

    public string CandidateDisplayCountText
        => $"{CountCandidateGroups(Candidates)} / {CountCandidateGroups(ReviewTargetCandidates)} グループ";

    /// <summary>ジャンルFilterだけを解除し、他の表示Filterは維持する。</summary>
    public void ClearGenreFilter()
    {
        var changed = false;
        foreach (var option in GenreOptions.Where(option => option.IsSelected).ToArray())
        {
            option.IsSelected = false;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        GenreFilterSearchText = string.Empty;
    }

    private void RefreshGenreOptions()
    {
        var selectedKeys = GenreOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trackGenres = BuildTrackGenres(ReviewTargetCandidates);
        var variants = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var hasMissing = false;

        foreach (var genres in trackGenres.Values)
        {
            var normalizedGenres = NormalizeGenres(genres).ToArray();
            if (normalizedGenres.Length == 0)
            {
                hasMissing = true;
                continue;
            }

            foreach (var genre in normalizedGenres)
            {
                if (!variants.TryGetValue(genre, out var spellings))
                {
                    spellings = new Dictionary<string, int>(StringComparer.Ordinal);
                    variants.Add(genre, spellings);
                }

                spellings[genre] = spellings.GetValueOrDefault(genre) + 1;
            }
        }

        GenreOptions.Clear();
        foreach (var pair in variants.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            // 比較はcase-insensitiveにするが、画面には実データで最も多い表記を残す。
            // 同数の場合は安定した表示になるよう文字列順で決定する。
            var displayName = pair.Value
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.CurrentCulture)
                .First().Key;
            GenreOptions.Add(new GenreFilterOptionViewModel(
                pair.Key,
                displayName,
                selectedKeys.Contains(pair.Key),
                OnGenreSelectionChanged));
        }

        if (hasMissing)
        {
            GenreOptions.Add(new GenreFilterOptionViewModel(
                MissingGenreKey,
                MissingGenreDisplayName,
                selectedKeys.Contains(MissingGenreKey),
                OnGenreSelectionChanged));
        }

        RefreshFilteredGenreOptions();
        NotifyGenreFilterStateChanged();
    }

    private void OnGenreSelectionChanged()
    {
        NotifyGenreFilterStateChanged();
        ApplyCandidateFilter();
    }

    private void NotifyGenreFilterStateChanged()
    {
        OnPropertyChanged(nameof(HasGenreFilter));
        OnPropertyChanged(nameof(GenreFilterSummary));
    }

    private void RefreshFilteredGenreOptions()
    {
        var search = GenreFilterSearchText.Trim();
        FilteredGenreOptions.Clear();
        foreach (var option in GenreOptions.Where(option =>
                     search.Length == 0
                     || option.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
        {
            FilteredGenreOptions.Add(option);
        }
    }

    private IEnumerable<CandidateReviewItemViewModel> ApplyGenreFilter(
        IEnumerable<CandidateReviewItemViewModel> candidates)
    {
        var source = candidates.ToArray();
        var selectedKeys = GenreOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedKeys.Count == 0)
        {
            return source;
        }

        // Group境界はレビュー状態タブで変化させない。まず一致度下限内の全CandidateからGroupを構築し、
        // その後で現在タブのCandidateへ表示Filterを適用する。
        var groupSource = ReviewTargetCandidates.ToArray();
        var trackGenres = BuildTrackGenres(groupSource);
        var adjacency = BuildCandidateAdjacency(groupSource);
        var matchingTrackIds = new HashSet<long>();
        var visited = new HashSet<long>();

        foreach (var trackId in adjacency.Keys)
        {
            if (!visited.Add(trackId))
            {
                continue;
            }

            var component = CollectComponent(trackId, adjacency, visited);
            var groupGenreKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var componentTrackId in component)
            {
                if (!trackGenres.TryGetValue(componentTrackId, out var genres)
                    || !NormalizeGenres(genres).Any())
                {
                    groupGenreKeys.Add(MissingGenreKey);
                    continue;
                }

                groupGenreKeys.UnionWith(NormalizeGenres(genres));
            }

            var matches = SelectedGenreFilterMode == GenreFilterMode.And
                ? selectedKeys.All(groupGenreKeys.Contains)
                : selectedKeys.Any(groupGenreKeys.Contains);
            if (matches)
            {
                matchingTrackIds.UnionWith(component);
            }
        }

        // ジャンルTagは誤設定されている可能性があるため、該当Trackを含むPairだけに絞らない。
        // 一致した重複候補Groupは全Pairを表示し、Tag違いのTrackが実は最良音質だった場合でも
        // 比較対象から消えて誤ったKeep判断につながらないようにする。
        return source.Where(item =>
            matchingTrackIds.Contains(item.TrackIdA)
            && matchingTrackIds.Contains(item.TrackIdB));
    }

    private static Dictionary<long, IReadOnlyList<string>> BuildTrackGenres(
        IEnumerable<CandidateReviewItemViewModel> candidates)
    {
        var result = new Dictionary<long, IReadOnlyList<string>>();
        foreach (var item in candidates)
        {
            result.TryAdd(item.TrackIdA, item.Row.GenresA);
            result.TryAdd(item.TrackIdB, item.Row.GenresB);
        }

        return result;
    }

    private static Dictionary<long, HashSet<long>> BuildCandidateAdjacency(
        IEnumerable<CandidateReviewItemViewModel> candidates)
    {
        var result = new Dictionary<long, HashSet<long>>();
        foreach (var item in candidates)
        {
            if (!result.TryGetValue(item.TrackIdA, out var left))
            {
                left = [];
                result.Add(item.TrackIdA, left);
            }

            if (!result.TryGetValue(item.TrackIdB, out var right))
            {
                right = [];
                result.Add(item.TrackIdB, right);
            }

            left.Add(item.TrackIdB);
            right.Add(item.TrackIdA);
        }

        return result;
    }

    private static HashSet<long> CollectComponent(
        long start,
        IReadOnlyDictionary<long, HashSet<long>> adjacency,
        HashSet<long> visited)
    {
        var result = new HashSet<long> { start };
        var pending = new Stack<long>();
        pending.Push(start);

        while (pending.TryPop(out var current))
        {
            foreach (var next in adjacency[current])
            {
                result.Add(next);
                if (visited.Add(next))
                {
                    pending.Push(next);
                }
            }
        }

        return result;
    }

    private static int CountCandidateGroups(IEnumerable<CandidateReviewItemViewModel> candidates)
    {
        var adjacency = BuildCandidateAdjacency(candidates);
        var visited = new HashSet<long>();
        var count = 0;
        foreach (var trackId in adjacency.Keys)
        {
            if (!visited.Add(trackId))
            {
                continue;
            }

            CollectComponent(trackId, adjacency, visited);
            count++;
        }

        return count;
    }

    private static IEnumerable<string> NormalizeGenres(IEnumerable<string> genres)
        => genres
            .Select(genre => genre.Trim())
            .Where(genre => genre.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// ジャンルFilterの結合方法をComboBoxへ表示する項目。
/// </summary>
public sealed record GenreFilterModeItem(GenreFilterMode Mode, string Label);
