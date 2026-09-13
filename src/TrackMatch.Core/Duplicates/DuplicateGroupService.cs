using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// 候補レビューの変更と、それに従う重複グループ再構成を一貫して扱う。
/// </summary>
public sealed class DuplicateGroupService(
    ICandidateReviewMutationRepository reviewRepository,
    ITrackLookupRepository trackLookupRepository,
    IDuplicateGroupRepository groupRepository)
{
    /// <summary>
    /// レビュー変更後のグループ構成を先に検証し、矛盾がなければレビューとグループを更新する。
    /// </summary>
    public async Task SaveReviewAsync(
        long libraryId,
        CandidateReview review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        review.Validate();
        await EnsurePairBelongsToLibraryAsync(libraryId, review.Pair, cancellationToken);

        var currentReviews = await GetLibraryReviewsAsync(libraryId, cancellationToken);
        var proposedReviews = currentReviews
            .Where(item => item.Pair != review.Pair)
            .Append(review)
            .ToArray();
        var existingGroups = await groupRepository.GetByLibraryIdAsync(libraryId, cancellationToken);
        var preferredKeep = review.Decision == CandidateReviewDecision.ConfirmedDuplicate
            ? review.KeepTrackId
            : null;
        var rebuild = DuplicateGroupPlanner.Build(proposedReviews, existingGroups, preferredKeep);

        // グループ再計算を先に完了させて矛盾を検出してから永続化する。
        // これによりNotDuplicateと推移的なConfirmedDuplicateが同時に成立する状態を作らない。
        await reviewRepository.SaveAsync(review, cancellationToken);
        await groupRepository.ReplaceLibraryAsync(libraryId, rebuild, cancellationToken);
    }

    /// <summary>
    /// レビューを未確定へ戻し、残っているConfirmedDuplicateだけからグループを再構成する。
    /// </summary>
    public async Task DeleteReviewAsync(
        long libraryId,
        CandidatePairKey pair,
        CancellationToken cancellationToken = default)
    {
        await EnsurePairBelongsToLibraryAsync(libraryId, pair, cancellationToken);

        var currentReviews = await GetLibraryReviewsAsync(libraryId, cancellationToken);
        var proposedReviews = currentReviews.Where(item => item.Pair != pair).ToArray();
        var existingGroups = await groupRepository.GetByLibraryIdAsync(libraryId, cancellationToken);
        var rebuild = DuplicateGroupPlanner.Build(proposedReviews, existingGroups);

        await reviewRepository.DeleteAsync(pair, cancellationToken);
        await groupRepository.ReplaceLibraryAsync(libraryId, rebuild, cancellationToken);
    }

    /// <summary>
    /// 現在保存されているレビューを正として重複グループを再同期する。
    /// </summary>
    /// <remarks>
    /// Trash実行前にも呼び出すことで、前回更新が途中で失敗した場合でも古いグループ状態を削除判断へ使用しない。
    /// </remarks>
    public async Task SynchronizeAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        var reviews = await GetLibraryReviewsAsync(libraryId, cancellationToken);
        var existingGroups = await groupRepository.GetByLibraryIdAsync(libraryId, cancellationToken);
        var rebuild = DuplicateGroupPlanner.Build(reviews, existingGroups);
        await groupRepository.ReplaceLibraryAsync(libraryId, rebuild, cancellationToken);
    }

    private async Task<IReadOnlyList<CandidateReview>> GetLibraryReviewsAsync(
        long libraryId,
        CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        var allReviews = await reviewRepository.GetAllAsync(cancellationToken);
        var trackCache = new Dictionary<long, StoredTrack?>();
        var result = new List<CandidateReview>();

        foreach (var review in allReviews)
        {
            var trackA = await GetTrackCachedAsync(review.Pair.TrackIdA, trackCache, cancellationToken);
            var trackB = await GetTrackCachedAsync(review.Pair.TrackIdB, trackCache, cancellationToken);
            if (trackA?.LibraryId == libraryId && trackB?.LibraryId == libraryId)
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

        if (trackA.LibraryId != libraryId || trackB.LibraryId != libraryId)
        {
            throw new InvalidOperationException("異なるLibraryのTrackを同じ重複レビューとして扱うことはできません。");
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
