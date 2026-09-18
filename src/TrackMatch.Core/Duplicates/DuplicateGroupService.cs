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
        PreferenceGraphEvaluator.EnsureAcyclic(proposed);
        var existingGroups = await groupRepository.GetAllGlobalAsync(cancellationToken);
        var active = await GetActiveGlobalReviewsAsync(proposed, cancellationToken);
        var rebuild = DuplicateGroupPlanner.Build(active, existingGroups);
        await reviewRepository.SaveAsync(review, libraryId, cancellationToken);
        try
        {
            if (HasTopologyChanged(rebuild, existingGroups)) await groupRepository.ReplaceGlobalAsync(rebuild, CancellationToken.None);
            await ApplyDerivedKeepAsync(libraryId, review.Pair.TrackIdA, proposed, CancellationToken.None);
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
            await ApplyDerivedKeepAsync(libraryId, pair.TrackIdA, proposed, CancellationToken.None);
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
    }

    private async Task ApplyDerivedKeepAsync(long libraryId, long trackId, IReadOnlyCollection<CandidateReview> reviews, CancellationToken cancellationToken)
    {
        var group = await groupRepository.GetByTrackIdAsync(trackId, libraryId, cancellationToken);
        if (group is null) return;

        // ProjectionのTrackIdsにはMissing Trackも残り得るため、Keep候補は実際に利用可能なTrackだけから導出する。
        // Library外のPreferred TrackもGlobal Groupの優劣関係には参加するので、対象集合自体はGlobalTrackIdsを使う。
        var activeTrackIds = new List<long>(group.GlobalTrackIds.Count);
        foreach (var groupTrackId in group.GlobalTrackIds)
        {
            var track = await trackLookupRepository.GetByIdAsync(groupTrackId, cancellationToken);
            if (track is not null && !track.IsMissing)
            {
                activeTrackIds.Add(groupTrackId);
            }
        }

        var candidates = PreferenceGraphEvaluator.GetKeepCandidates(reviews, activeTrackIds);
        if (candidates.Count == 1 && group.KeepTrackId != candidates[0])
        {
            await groupRepository.SetKeepAsync(libraryId, group.Id, candidates[0], "HumanVerdict", cancellationToken);
        }
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
            if (a is not null && b is not null && !a.IsMissing && !b.IsMissing) result.Add(review);
        }
        return result;
    }

    private async Task EnsurePairBelongsToLibraryAsync(long libraryId, CandidatePairKey pair, bool requireActiveTracks, CancellationToken cancellationToken)
    {
        if (libraryId <= 0) throw new ArgumentOutOfRangeException(nameof(libraryId));
        var a = await trackLookupRepository.GetByIdAsync(pair.TrackIdA, cancellationToken);
        var b = await trackLookupRepository.GetByIdAsync(pair.TrackIdB, cancellationToken);
        if (a is null || b is null) throw new InvalidOperationException("レビュー対象のTrackが見つかりません。");
        if (requireActiveTracks && (a.IsMissing || b.IsMissing)) throw new InvalidOperationException("Missing状態のTrackへ新しいHuman Verdictを保存できません。");
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
