using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// Global Human Verdictの変更と派生Global Duplicate Groupの整合を管理する。
/// </summary>
public sealed class DuplicateGroupService(
    ICandidateReviewMutationRepository reviewRepository,
    ITrackLookupRepository trackLookupRepository,
    IDuplicateGroupRepository groupRepository)
{
    /// <summary>Human Verdictを保存し、ConfirmedDuplicate Graphから派生Groupを再構成する。</summary>
    public async Task SaveReviewAsync(long libraryId, CandidateReview review, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        review.Validate();
        await EnsurePairBelongsToLibraryAsync(libraryId, review.Pair, true, cancellationToken);
        var allReviews = await reviewRepository.GetAllAsync(cancellationToken);
        if (allReviews.SingleOrDefault(item => item.Pair == review.Pair) == review) return;
        var proposed = allReviews.Where(item => item.Pair != review.Pair).Append(review).ToArray();
        // 一時利用停止中のVerdictも将来復帰するCurrent Human Verdictであるため、循環不変条件だけは全Current集合で検証する。
        PreferenceGraphEvaluator.EnsureAcyclic(proposed);
        var active = await GetActiveGlobalReviewsAsync(proposed, cancellationToken);
        var existingGroups = await groupRepository.GetAllGlobalAsync(cancellationToken);
        var rebuild = DuplicateGroupPlanner.Build(active, existingGroups);
        await reviewRepository.SaveAsync(review, libraryId, cancellationToken);
        try
        {
            if (HasTopologyChanged(rebuild, existingGroups)) await groupRepository.ReplaceGlobalAsync(rebuild, CancellationToken.None);
            await ApplyDerivedKeepForLibraryAsync(libraryId, active, CancellationToken.None);
            await ApplyDerivedKeepForAllLibrariesAsync(active, libraryId, CancellationToken.None);
        }
        catch
        {
            await TryRepairGlobalTopologyAsync();
            throw;
        }
    }

    /// <summary>Human Verdictを未確定へ戻し、残った判定から派生Groupを再構成する。</summary>
    public async Task DeleteReviewAsync(long libraryId, CandidatePairKey pair, CancellationToken cancellationToken = default)
    {
        await EnsurePairBelongsToLibraryAsync(libraryId, pair, false, cancellationToken);
        var current = await GetActiveGlobalReviewsAsync(cancellationToken);
        var proposed = current.Where(item => item.Pair != pair).ToArray();
        var existingGroups = await groupRepository.GetAllGlobalAsync(cancellationToken);
        var rebuild = DuplicateGroupPlanner.Build(proposed, existingGroups);
        await reviewRepository.DeleteAsync(pair, libraryId, cancellationToken);
        try
        {
            if (HasTopologyChanged(rebuild, existingGroups)) await groupRepository.ReplaceGlobalAsync(rebuild, CancellationToken.None);
            await ApplyDerivedKeepForLibraryAsync(libraryId, proposed, CancellationToken.None);
            await ApplyDerivedKeepForAllLibrariesAsync(proposed, libraryId, CancellationToken.None);
        }
        catch
        {
            await TryRepairGlobalTopologyAsync();
            throw;
        }
    }

    /// <summary>Current Human Verdictから派生Global Groupを再同期する。</summary>
    public async Task SynchronizeGlobalAsync(CancellationToken cancellationToken = default)
    {
        var reviews = await GetActiveGlobalReviewsAsync(cancellationToken);
        PreferenceGraphEvaluator.EnsureAcyclic(reviews);
        var existingGroups = await groupRepository.GetAllGlobalAsync(cancellationToken);
        var rebuild = DuplicateGroupPlanner.Build(reviews, existingGroups);
        if (HasTopologyChanged(rebuild, existingGroups)) await groupRepository.ReplaceGlobalAsync(rebuild, cancellationToken);
        await ApplyDerivedKeepForAllLibrariesAsync(reviews, excludedLibraryId: null, cancellationToken);
    }

    private async Task ApplyDerivedKeepForAllLibrariesAsync(
        IReadOnlyCollection<CandidateReview> reviews,
        long? excludedLibraryId,
        CancellationToken cancellationToken)
    {
        foreach (var libraryId in await groupRepository.GetLibraryIdsAsync(cancellationToken))
        {
            if (libraryId != excludedLibraryId)
            {
                await ApplyDerivedKeepForLibraryAsync(libraryId, reviews, cancellationToken);
            }
        }
    }

    private async Task ApplyDerivedKeepForLibraryAsync(
        long libraryId,
        IReadOnlyCollection<CandidateReview> reviews,
        CancellationToken cancellationToken)
    {
        var groups = await groupRepository.GetByLibraryIdAsync(libraryId, cancellationToken);
        foreach (var group in groups)
        {
            await ApplyDerivedKeepAsync(libraryId, group.Id, reviews, cancellationToken);
        }
    }

    private async Task ApplyDerivedKeepAsync(long libraryId, long groupId, IReadOnlyCollection<CandidateReview> reviews, CancellationToken cancellationToken)
    {
        var group = await groupRepository.GetByIdAsync(groupId, libraryId, cancellationToken);
        if (group is null) return;

        // KeepはLibrary固有なので、現在LibraryのMembershipに存在するTrackだけを候補にする。
        // Library外のPreferred Trackだけを理由に、現在Libraryで実際に残せるTrackを候補から落としてはならない。
        var activeTrackIds = new List<long>(group.TrackIds.Count);
        foreach (var groupTrackId in group.TrackIds)
        {
            var track = await trackLookupRepository.GetByIdAsync(groupTrackId, cancellationToken);
            if (track is not null && !track.IsMissing)
            {
                activeTrackIds.Add(groupTrackId);
            }
        }

        var groupTrackIds = group.GlobalTrackIds.ToHashSet();
        var groupReviews = reviews
            .Where(review => groupTrackIds.Contains(review.Pair.TrackIdA)
                && groupTrackIds.Contains(review.Pair.TrackIdB))
            .ToArray();
        var hasConflict = DuplicateGroupConflictEvaluator.FindConflicts(groupReviews).Count != 0;
        if (hasConflict)
        {
            // NotDuplicateとの矛盾はHuman Verdictを破棄せず正常なConflict状態として投影し、Trashを安全側で停止する。
            await groupRepository.SetDerivedKeepStateAsync(
                libraryId,
                group.Id,
                null,
                DuplicateGroupKeepStatus.Conflict,
                "HumanVerdict",
                cancellationToken);
            return;
        }

        var candidates = PreferenceGraphEvaluator.GetKeepCandidates(groupReviews, activeTrackIds);
        await groupRepository.SetDerivedKeepStateAsync(
            libraryId,
            group.Id,
            candidates.Count == 1 ? candidates[0] : null,
            candidates.Count == 1 ? DuplicateGroupKeepStatus.Selected : DuplicateGroupKeepStatus.Unselected,
            "HumanVerdict",
            cancellationToken);
    }

    private async Task TryRepairGlobalTopologyAsync()
    {
        try { await SynchronizeGlobalAsync(CancellationToken.None); }
        catch { }
    }

    private async Task<IReadOnlyList<CandidateReview>> GetActiveGlobalReviewsAsync(CancellationToken cancellationToken)
        => await GetActiveGlobalReviewsAsync(await reviewRepository.GetAllAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<CandidateReview>> GetActiveGlobalReviewsAsync(IReadOnlyCollection<CandidateReview> allReviews, CancellationToken cancellationToken)
    {
        var result = new List<CandidateReview>(allReviews.Count);
        var cache = new Dictionary<long, StoredTrack?>();
        foreach (var review in allReviews)
        {
            var a = await GetTrackCachedAsync(review.Pair.TrackIdA, cache, cancellationToken);
            var b = await GetTrackCachedAsync(review.Pair.TrackIdB, cache, cancellationToken);
            if (a is not null && b is not null
                && !a.IsMissing && !b.IsMissing
                && await trackLookupRepository.IsHumanVerdictUsableAsync(review.Pair.TrackIdA, cancellationToken)
                && await trackLookupRepository.IsHumanVerdictUsableAsync(review.Pair.TrackIdB, cancellationToken))
            {
                result.Add(review);
            }
        }
        return result;
    }

    private async Task EnsurePairBelongsToLibraryAsync(long libraryId, CandidatePairKey pair, bool requireActiveTracks, CancellationToken cancellationToken)
    {
        if (libraryId <= 0) throw new ArgumentOutOfRangeException(nameof(libraryId));
        var a = await trackLookupRepository.GetByIdAsync(pair.TrackIdA, cancellationToken);
        var b = await trackLookupRepository.GetByIdAsync(pair.TrackIdB, cancellationToken);
        if (a is null || b is null) throw new InvalidOperationException("レビュー対象のTrackが見つかりません。");
        if (requireActiveTracks && (a.IsMissing || b.IsMissing))
        {
            throw new InvalidOperationException("Missing状態のTrackへ新しいHuman Verdictを保存できません。");
        }

        if (requireActiveTracks
            && (!await trackLookupRepository.IsHumanVerdictUsableAsync(pair.TrackIdA, cancellationToken)
                || !await trackLookupRepository.IsHumanVerdictUsableAsync(pair.TrackIdB, cancellationToken)))
        {
            throw new InvalidOperationException("Content Verificationまたは再評価中のTrackへHuman Verdictを保存できません。");
        }
        if (!await trackLookupRepository.IsInLibraryAsync(pair.TrackIdA, libraryId, cancellationToken)
            || !await trackLookupRepository.IsInLibraryAsync(pair.TrackIdB, libraryId, cancellationToken))
        {
            throw new InvalidOperationException("現在LibraryのMembership外Track同士を通常レビューとして扱うことはできません。");
        }
    }

    private async Task<StoredTrack?> GetTrackCachedAsync(long trackId, IDictionary<long, StoredTrack?> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(trackId, out var cached)) return cached;
        var track = await trackLookupRepository.GetByIdAsync(trackId, cancellationToken);
        cache[trackId] = track;
        return track;
    }

    private static bool HasTopologyChanged(IReadOnlyCollection<DuplicateGroupRebuildItem> rebuild, IReadOnlyCollection<GlobalDuplicateGroup> existingGroups)
    {
        if (rebuild.Count != existingGroups.Count) return true;
        var existingById = existingGroups.ToDictionary(group => group.Id);
        return rebuild.Any(plan => plan.ExistingGroupId is not { } groupId
            || !existingById.TryGetValue(groupId, out var existing)
            || !plan.TrackIds.Order().SequenceEqual(existing.TrackIds.Order()));
    }
}
