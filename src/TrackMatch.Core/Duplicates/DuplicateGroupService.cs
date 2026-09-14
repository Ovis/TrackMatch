using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// Global Human Verdictの変更とGlobal Duplicate Group、Library固有Keepの整合を管理する。
/// </summary>
public sealed class DuplicateGroupService(
    ICandidateReviewMutationRepository reviewRepository,
    ITrackLookupRepository trackLookupRepository,
    IDuplicateGroupRepository groupRepository)
{
    /// <summary>
    /// Keep選択を伴わないGlobal Verdictを保存する。
    /// </summary>
    /// <remarks>
    /// ConfirmedDuplicateではLibrary固有Keepが必要なため、Keepを受け取るOverloadを使用する。
    /// </remarks>
    public Task SaveReviewAsync(
        long libraryId,
        CandidateReview review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.Decision == CandidateReviewDecision.ConfirmedDuplicate)
        {
            throw new ArgumentException("ConfirmedDuplicateではLibrary固有Keep Trackを指定してください。", nameof(review));
        }

        return SaveReviewAsync(libraryId, review, keepTrackId: null, cancellationToken);
    }

    /// <summary>
    /// Global Verdictを保存し、Global Groupを再構成したうえで現在LibraryのKeepを反映する。
    /// </summary>
    /// <param name="libraryId">操作元Library ID</param>
    /// <param name="review">Global Track Pairへ保存するHuman Verdict</param>
    /// <param name="keepTrackId">ConfirmedDuplicate時に現在Libraryで残すTrack ID</param>
    /// <param name="cancellationToken">Verdict Commit前までのキャンセル要求</param>
    public async Task SaveReviewAsync(
        long libraryId,
        CandidateReview review,
        long? keepTrackId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        review.Validate();
        ValidateKeepSelection(review, keepTrackId);
        await EnsurePairBelongsToLibraryAsync(libraryId, review.Pair, cancellationToken);

        var currentReviews = await GetActiveGlobalReviewsAsync(cancellationToken);
        var proposedReviews = currentReviews
            .Where(item => item.Pair != review.Pair)
            .Append(review)
            .ToArray();
        var existingGroups = await groupRepository.GetAllGlobalAsync(cancellationToken);
        var rebuild = DuplicateGroupPlanner.Build(proposedReviews, existingGroups);

        // 矛盾検証を終えてからCurrent Verdictを先にCommitする。
        // 以降はCurrent Verdictが正本になっているため、呼び出し元Cancelで派生Group更新だけを中断しない。
        await reviewRepository.SaveAsync(review, libraryId, cancellationToken);
        try
        {
            if (HasTopologyChanged(rebuild, existingGroups))
            {
                await groupRepository.ReplaceGlobalAsync(rebuild, CancellationToken.None);
            }

            if (review.Decision != CandidateReviewDecision.ConfirmedDuplicate || keepTrackId is null)
            {
                return;
            }

            var group = await groupRepository.GetByTrackIdAsync(review.Pair.TrackIdA, libraryId, CancellationToken.None)
                ?? throw new InvalidOperationException("保存した重複判定からGlobal Duplicate Groupを解決できませんでした。");

            // 既存Group同士の結合でKeepが競合した場合は、今回のPair操作だけで勝手に競合を解消しない。
            // 新規Groupまたは競合していないGroupでは、ユーザーが押したA/Bを現在Libraryの明示Keepとして反映する。
            if (group.KeepStatus != DuplicateGroupKeepStatus.Conflict)
            {
                await groupRepository.SetKeepAsync(
                    libraryId,
                    group.Id,
                    keepTrackId.Value,
                    "UserReview",
                    CancellationToken.None);
            }
        }
        catch
        {
            // Verdict Commit後に派生更新だけ失敗した場合は、Current Verdictを正本として最低限Topologyを再同期する。
            // Keep設定まで失敗したケースは安全側で未選択/既存状態として残し、呼び出し元へ例外を返して再操作を促す。
            await TryRepairGlobalTopologyAsync();
            throw;
        }
    }

    /// <summary>
    /// Global Verdictを未確定へ戻し、残っているCurrent VerdictからGlobal Groupを再構成する。
    /// </summary>
    public async Task DeleteReviewAsync(
        long libraryId,
        CandidatePairKey pair,
        CancellationToken cancellationToken = default)
    {
        await EnsurePairBelongsToLibraryAsync(libraryId, pair, cancellationToken);

        var currentReviews = await GetActiveGlobalReviewsAsync(cancellationToken);
        var proposedReviews = currentReviews.Where(item => item.Pair != pair).ToArray();
        var existingGroups = await groupRepository.GetAllGlobalAsync(cancellationToken);
        var rebuild = DuplicateGroupPlanner.Build(proposedReviews, existingGroups);

        await reviewRepository.DeleteAsync(pair, libraryId, cancellationToken);
        try
        {
            if (HasTopologyChanged(rebuild, existingGroups))
            {
                await groupRepository.ReplaceGlobalAsync(rebuild, CancellationToken.None);
            }
        }
        catch
        {
            await TryRepairGlobalTopologyAsync();
            throw;
        }
    }

    /// <summary>
    /// Current Global Verdictを正としてGlobal Duplicate Groupを再同期する。
    /// </summary>
    /// <remarks>
    /// Missing Trackを含むVerdictは履歴価値を残したままCurrent Group形成から除外する。
    /// Trash直前にも呼び出すことで、物理状態が変わったTrackを削除判断に使わない。
    /// </remarks>
    public Task SynchronizeAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        return SynchronizeGlobalAsync(cancellationToken);
    }

    /// <summary>
    /// Library Contextを必要としない管理操作後に、Current Global VerdictからGlobal Groupを再同期する。
    /// </summary>
    public async Task SynchronizeGlobalAsync(CancellationToken cancellationToken = default)
    {
        var reviews = await GetActiveGlobalReviewsAsync(cancellationToken);
        var existingGroups = await groupRepository.GetAllGlobalAsync(cancellationToken);
        var rebuild = DuplicateGroupPlanner.Build(reviews, existingGroups);

        // Scan完了ごとに同一構成をDELETE/INSERTするとKeep Historyへ意味のないGroupRebuildが蓄積する。
        // Track集合と継承Group IDが完全一致する場合は、派生状態が既にCurrent Verdictと整合しているため書き換えない。
        if (HasTopologyChanged(rebuild, existingGroups))
        {
            await groupRepository.ReplaceGlobalAsync(rebuild, cancellationToken);
        }
    }

    private async Task TryRepairGlobalTopologyAsync()
    {
        try
        {
            await SynchronizeGlobalAsync(CancellationToken.None);
        }
        catch
        {
            // 元の派生更新失敗を呼び出し元へ返すことを優先する。
            // DB障害が継続している場合、ここで別例外へ置き換えると最初の失敗原因を失うため握りつぶす。
        }
    }

    private async Task<IReadOnlyList<CandidateReview>> GetActiveGlobalReviewsAsync(
        CancellationToken cancellationToken)
    {
        var allReviews = await reviewRepository.GetAllAsync(cancellationToken);
        var result = new List<CandidateReview>(allReviews.Count);
        var cache = new Dictionary<long, StoredTrack?>();

        foreach (var review in allReviews)
        {
            var trackA = await GetTrackCachedAsync(review.Pair.TrackIdA, cache, cancellationToken);
            var trackB = await GetTrackCachedAsync(review.Pair.TrackIdB, cache, cancellationToken);
            if (trackA is not null && trackB is not null && !trackA.IsMissing && !trackB.IsMissing)
            {
                result.Add(review);
            }
        }

        return result;
    }

    private async Task EnsurePairBelongsToLibraryAsync(
        long libraryId,
        CandidatePairKey pair,
        CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        var trackA = await trackLookupRepository.GetByIdAsync(pair.TrackIdA, cancellationToken);
        var trackB = await trackLookupRepository.GetByIdAsync(pair.TrackIdB, cancellationToken);
        if (trackA is null || trackB is null)
        {
            throw new InvalidOperationException("レビュー対象のTrackが見つかりません。");
        }

        if (!await trackLookupRepository.IsInLibraryAsync(pair.TrackIdA, libraryId, cancellationToken)
            || !await trackLookupRepository.IsInLibraryAsync(pair.TrackIdB, libraryId, cancellationToken))
        {
            throw new InvalidOperationException("現在LibraryのMembership外Track同士を通常レビューとして扱うことはできません。");
        }
    }

    private async Task<StoredTrack?> GetTrackCachedAsync(
        long trackId,
        IDictionary<long, StoredTrack?> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(trackId, out var cached))
        {
            return cached;
        }

        var track = await trackLookupRepository.GetByIdAsync(trackId, cancellationToken);
        cache[trackId] = track;
        return track;
    }

    private static void ValidateKeepSelection(CandidateReview review, long? keepTrackId)
    {
        if (review.Decision == CandidateReviewDecision.NotDuplicate)
        {
            if (keepTrackId is not null)
            {
                throw new ArgumentException("NotDuplicateではLibrary Keep Trackを指定できません。", nameof(keepTrackId));
            }

            return;
        }

        if (keepTrackId is null)
        {
            throw new ArgumentException("ConfirmedDuplicateではLibrary Keep Trackが必要です。", nameof(keepTrackId));
        }

        if (keepTrackId != review.Pair.TrackIdA && keepTrackId != review.Pair.TrackIdB)
        {
            throw new ArgumentException("Candidate Review操作で選ぶKeep TrackはPairを構成するTrackのいずれかである必要があります。", nameof(keepTrackId));
        }
    }

    private static bool HasTopologyChanged(
        IReadOnlyCollection<DuplicateGroupRebuildItem> rebuild,
        IReadOnlyCollection<GlobalDuplicateGroup> existingGroups)
    {
        if (rebuild.Count != existingGroups.Count)
        {
            return true;
        }

        var existingById = existingGroups.ToDictionary(group => group.Id);
        foreach (var plan in rebuild)
        {
            if (plan.ExistingGroupId is not { } groupId
                || !existingById.TryGetValue(groupId, out var existing)
                || !plan.TrackIds.Order().SequenceEqual(existing.TrackIds.Order()))
            {
                return true;
            }
        }

        return false;
    }
}
