using System.Collections.ObjectModel;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Persistence;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App;

public sealed partial class MainWindowViewModel
{
    private int _duplicateGroupLoadVersion;

    /// <summary>
    /// 現在のCandidate A/Bのいずれかが所属する確定済み重複グループ。
    /// A/Bが別グループの場合は2件表示し、結合前でも双方を確認できるようにする。
    /// </summary>
    public ObservableCollection<DuplicateGroupSummaryViewModel> DuplicateGroups { get; } = [];

    public bool HasDuplicateGroups => DuplicateGroups.Count > 0;

    /// <summary>
    /// 指定TrackをKeepにする操作が既存グループへ与える影響を、確認Dialog向けの文面として返す。
    /// 既存Keepに変化がない場合はnullを返す。
    /// </summary>
    public async Task<string?> GetKeepChangeImpactAsync(long keepTrackId)
    {
        var selected = SelectedCandidate;
        var library = SelectedLibrary;
        if (selected is null || library is null)
        {
            return null;
        }

        var database = new SqliteDatabase(DatabasePath);
        await database.InitializeAsync();
        var groupRepository = new SqliteDuplicateGroupRepository(database);
        var groups = await GetGroupsForCandidateAsync(groupRepository, selected);
        if (groups.Count == 0)
        {
            return null;
        }

        if (groups.Count == 1 && groups[0].KeepTrackId == keepTrackId)
        {
            return null;
        }

        var tracks = new SqliteTrackLookupRepository(database);
        var newKeep = await tracks.GetByIdAsync(keepTrackId);
        var newKeepTitle = FormatTrackTitle(newKeep, keepTrackId);

        if (groups.Count == 1)
        {
            var currentKeep = await tracks.GetByIdAsync(groups[0].KeepTrackId);
            return $"この操作により、重複グループ #{groups[0].Id} の残すファイルが「{FormatTrackTitle(currentKeep, groups[0].KeepTrackId)}」から「{newKeepTitle}」に変更されます。";
        }

        var groupIds = string.Join(" と ", groups.Select(group => $"#{group.Id}"));
        return $"この操作により、重複グループ {groupIds} が結合され、結合後に残すファイルは「{newKeepTitle}」になります。";
    }

    private async Task LoadDuplicateGroupsForSelectionAsync(CandidateReviewItemViewModel? selected)
    {
        var version = ++_duplicateGroupLoadVersion;
        if (selected is null || SelectedLibrary is null || !File.Exists(DatabasePath))
        {
            ReplaceDuplicateGroups([]);
            return;
        }

        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            var groupRepository = new SqliteDuplicateGroupRepository(database);
            var groups = await GetGroupsForCandidateAsync(groupRepository, selected);
            var tracks = new SqliteTrackLookupRepository(database);
            var summaries = new List<DuplicateGroupSummaryViewModel>(groups.Count);

            foreach (var group in groups)
            {
                var keep = await tracks.GetByIdAsync(group.KeepTrackId);
                summaries.Add(new DuplicateGroupSummaryViewModel(
                    group.Id,
                    group.TrackIds.Count,
                    group.KeepTrackId,
                    FormatTrackTitle(keep, group.KeepTrackId),
                    keep?.Metadata.Year?.ToString()));
            }

            // Candidateを高速に切り替えた場合、遅れて完了した旧Queryで表示を巻き戻さない。
            if (version != _duplicateGroupLoadVersion || !ReferenceEquals(selected, SelectedCandidate))
            {
                return;
            }

            ReplaceDuplicateGroups(summaries);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            if (version == _duplicateGroupLoadVersion)
            {
                ReplaceDuplicateGroups([]);
                StatusText = $"重複グループ読み込み失敗: {exception.Message}";
            }
        }
    }

    private static async Task<IReadOnlyList<DuplicateGroup>> GetGroupsForCandidateAsync(
        IDuplicateGroupRepository repository,
        CandidateReviewItemViewModel selected)
    {
        var groupA = await repository.GetByTrackIdAsync(selected.TrackIdA);
        var groupB = await repository.GetByTrackIdAsync(selected.TrackIdB);
        return new[] { groupA, groupB }
            .Where(group => group is not null)
            .Select(group => group!)
            .GroupBy(group => group.Id)
            .Select(group => group.First())
            .OrderBy(group => group.Id)
            .ToArray();
    }

    private void ReplaceDuplicateGroups(IEnumerable<DuplicateGroupSummaryViewModel> groups)
    {
        DuplicateGroups.Clear();
        foreach (var group in groups)
        {
            DuplicateGroups.Add(group);
        }

        OnPropertyChanged(nameof(HasDuplicateGroups));
    }

    private static string FormatTrackTitle(StoredTrack? track, long trackId)
    {
        if (track is null)
        {
            return $"Track #{trackId}";
        }

        return string.IsNullOrWhiteSpace(track.Metadata.Title)
            ? Path.GetFileName(track.Metadata.Path)
            : track.Metadata.Title;
    }
}
