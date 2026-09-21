using System.Collections.ObjectModel;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App;

public sealed partial class MainWindowViewModel
{
    private int _duplicateGroupLoadVersion;

    /// <summary>
    /// 現在のCandidate A/Bのいずれかが所属するGlobal Duplicate GroupのLibrary Projection。
    /// </summary>
    public ObservableCollection<DuplicateGroupSummaryViewModel> DuplicateGroups { get; } = [];

    public bool HasDuplicateGroups => DuplicateGroups.Count > 0;

    /// <summary>
    /// 重複グループ詳細画面から戻った後、Track状態やHuman Verdict由来の派生状態をCandidate一覧へ反映する。
    /// </summary>
    public async Task RefreshDuplicateGroupsAsync()
    {
        var selected = SelectedCandidate;
        if (selected is not null)
        {
            // レビュー省略はHuman Verdictと派生Keepから再計算するため、右ペインだけでなくCandidate一覧も最新Projectionから再構築する。
            await ReloadCandidatesPreservingPairAsync(selected.TrackIdA, selected.TrackIdB);
        }

        await LoadDuplicateGroupsForSelectionAsync(SelectedCandidate);
    }

    /// <summary>
    /// 指定TrackをPreferredとするHuman Verdictが派生Keepへ与える影響を確認Dialog向け文面として返す。
    /// </summary>
    public async Task<string?> GetKeepChangeImpactAsync(long preferredTrackId)
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
        var groups = await GetGroupsForCandidateAsync(groupRepository, selected, library.Id);
        if (groups.Count == 0)
        {
            return null;
        }

        if (groups.Count > 1)
        {
            var groupIds = string.Join(" と ", groups.Select(group => $"#{group.Id}"));
            return $"このレビュー判定により重複グループ {groupIds} が結合されます。結合後の残すファイルは、すべての優劣関係から再計算されます。";
        }

        var group = groups[0];
        var pair = CandidatePairKey.Create(selected.TrackIdA, selected.TrackIdB);
        var proposedReviews = (await GetUsableReviewsAsync(database))
            .Where(review => review.Pair != pair)
            .Append(new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, preferredTrackId, null))
            .ToArray();
        PreferenceGraphEvaluator.EnsureAcyclic(proposedReviews);

        var globalTrackIds = group.GlobalTrackIds.ToHashSet();
        var groupReviews = proposedReviews
            .Where(review => globalTrackIds.Contains(review.Pair.TrackIdA)
                && globalTrackIds.Contains(review.Pair.TrackIdB))
            .ToArray();

        // Conflictや複数Top候補が残る場合に「このTrackでKeepが一意化する」と断定しない。
        // 確認文もHuman Verdictから同じ派生ロジックで計算し、保存後の実状態と食い違わないようにする。
        if (DuplicateGroupConflictEvaluator.FindConflicts(groupReviews).Count != 0)
        {
            return $"この優劣判定後も重複グループ #{group.Id} にはレビュー判定の矛盾が残るため、ファイル整理は引き続き停止されます。";
        }

        var keepCandidates = PreferenceGraphEvaluator.GetKeepCandidates(groupReviews, group.TrackIds);
        if (keepCandidates.Count != 1)
        {
            return $"この優劣判定後も重複グループ #{group.Id} の残す候補は {keepCandidates.Count} ファイル残ります。追加レビュー後に残すファイルが再計算されます。";
        }

        var derivedKeepTrackId = keepCandidates[0];
        if (group.KeepStatus == DuplicateGroupKeepStatus.Selected
            && group.KeepTrackId == derivedKeepTrackId)
        {
            return null;
        }

        var tracks = new SqliteTrackLookupRepository(database);
        var derivedKeep = await tracks.GetByIdAsync(derivedKeepTrackId);
        var derivedKeepName = FormatTrackFileName(derivedKeep, derivedKeepTrackId);
        if (group.KeepStatus != DuplicateGroupKeepStatus.Selected || group.KeepTrackId is null)
        {
            return $"この優劣判定により、重複グループ #{group.Id} の残すファイルが「{derivedKeepName}」に一意化されます。";
        }

        var currentKeep = await tracks.GetByIdAsync(group.KeepTrackId.Value);
        return $"この優劣判定により、重複グループ #{group.Id} の残すファイルが「{FormatTrackFileName(currentKeep, group.KeepTrackId.Value)}」から「{derivedKeepName}」へ変わります。";
    }

    private async Task LoadDuplicateGroupsForSelectionAsync(CandidateReviewItemViewModel? selected)
    {
        var version = ++_duplicateGroupLoadVersion;
        var library = SelectedLibrary;
        if (selected is null || library is null || !File.Exists(DatabasePath))
        {
            ReplaceDuplicateGroups([]);
            return;
        }

        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            var groupRepository = new SqliteDuplicateGroupRepository(database);
            var groups = await GetGroupsForCandidateAsync(groupRepository, selected, library.Id);
            var tracks = new SqliteTrackLookupRepository(database);
            var summaries = new List<DuplicateGroupSummaryViewModel>(groups.Count);

            foreach (var group in groups)
            {
                StoredTrack? keep = null;
                if (group.KeepTrackId is { } keepTrackId)
                {
                    keep = await tracks.GetByIdAsync(keepTrackId);
                }

                summaries.Add(new DuplicateGroupSummaryViewModel(
                    group.Id,
                    group.TrackIds.Count,
                    group.GlobalTrackIds.Count,
                    group.KeepTrackId,
                    group.KeepStatus,
                    group.KeepTrackId is { } id ? FormatTrackTitle(keep, id) : string.Empty,
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
        CandidateReviewItemViewModel selected,
        long libraryId)
    {
        var groupA = await repository.GetByTrackIdAsync(selected.TrackIdA, libraryId);
        var groupB = await repository.GetByTrackIdAsync(selected.TrackIdB, libraryId);
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

    /// <summary>Keep変更確認でTrackを一意に識別しやすいファイル名を返す。</summary>
    private static string FormatTrackFileName(StoredTrack? track, long trackId)
    {
        if (track is null)
        {
            return $"Track #{trackId}";
        }

        var fileName = Path.GetFileName(track.Metadata.Path);
        return string.IsNullOrWhiteSpace(fileName) ? $"Track #{trackId}" : fileName;
    }
}
