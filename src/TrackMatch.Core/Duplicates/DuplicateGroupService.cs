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
    /// Global Verdictを保存し、Global Groupを再構成したうえで現在LibraryのKeepを反映する。
    /// </summary>
    public async Task SaveReviewAsync(
        long libraryId,
        CandidateReview review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        review.Validate();
        await EnsurePairBelongsToLibraryAsync(libraryId, review.Pair, cancellationToken);

        var currentReviews = await GetActiveGlobalReviewsAsync(cancellationToken);
        var proposedReviews = currentReviews
            .Where(item => item.Pair != review.Pair)
            .Append(review)
            .ToArray();
        var existingGroups = await groupRepository.GetAllGlobalAsync(cancellationToken);
        var rebuild = DuplicateGroupPlanner.Build(proposedReviews, existingGroups);

        // 矛盾検証を終えてからCurrent Verdictと派生Groupを更新する。
        // Verdict自体はGlobalだが、HistoryでどのLibrary Contextから操作したか追えるよう現在Libraryも渡す。
        await reviewRepository.SaveAsync(review, libraryId, cancellationToken);
        await groupRepository.ReplaceGlobalAsync(rebuild, cancellationToken);

        if (review.Decision != CandidateReviewDecision.ConfirmedDuplicate || review.KeepTrackId is null)
        {
            return;
        }

        var group = await groupRepository.GetByTrackIdAsync(review.Pair.TrackIdA, libraryId, cancellationToken)
            ?? throw new InvalidOperationException("保存した重複判定からGlobal Duplicate Groupを解決できませんでした。");

        // 既存Group同士の結合でKeepが競合した場合は、今回のペア上のKeepで勝手に競合を解消しない。
        // 新規Groupまたは競合していないGroupでは、ユーザーが押したA/Bを現在Libraryの明示Keepとして反映する。
        if (group.KeepStatus != DuplicateGroupKeepStatus.Conflict)
        {
            await groupRepository.SetKeepAsync(
                libraryId,
                group.Id,
                review.KeepTrackId.Value,
                "UserReview",
                cancellationToken);
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
        await groupRepository.ReplaceGlobalAsync(rebuild, cancellationToken);
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
        await groupRepository.ReplaceGlobalAsync(rebuild, cancellationToken);
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
}
