using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Application;

/// <summary>
/// Human Verdict変更と、そのAffected Closureに限定した派生状態更新を統括する。
/// </summary>
public sealed class ReviewMutationService(
    ICandidateReviewMutationRepository reviewRepository,
    ITrackLookupRepository trackLookupRepository,
    IDuplicateGroupRepository groupRepository,
    DuplicateGroupService duplicateGroupService,
    SupplementalCandidateReconciliationService supplementalReconciliationService,
    ILogger<ReviewMutationService>? logger = null)
{
    private readonly ILogger<ReviewMutationService> _logger = logger ?? NullLogger<ReviewMutationService>.Instance;

    /// <summary>
    /// Human Verdictを保存し、レビュー対象のBefore/After ClosureだけをReview可能な状態へ収束させる。
    /// </summary>
    public async Task<ReviewMutationResult> SaveAsync(
        long libraryId,
        CandidateReview review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        review.Validate();
        await ValidatePairAsync(libraryId, review.Pair, requireActiveTracks: true, cancellationToken);

        var currentReviews = await reviewRepository.GetAllAsync(cancellationToken);
        var current = currentReviews.SingleOrDefault(item => item.Pair == review.Pair);
        if (current == review)
        {
            return CreateLocalResult(review.Pair);
        }

        if (current is not null
            && current.Decision == review.Decision
            && current.PreferredTrackId == review.PreferredTrackId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await reviewRepository.SaveAsync(review, libraryId, cancellationToken);
            return CreateLocalResult(review.Pair);
        }

        return await MutateAsync(
            libraryId,
            review.Pair,
            token => duplicateGroupService.SaveReviewAsync(libraryId, review, token),
            cancellationToken);
    }

    /// <summary>
    /// Current Human Verdictを削除し、残ったVerdictからAffected Closureを収束させる。
    /// </summary>
    public async Task<ReviewMutationResult> DeleteAsync(
        long libraryId,
        CandidatePairKey pair,
        CancellationToken cancellationToken = default)
    {
        await ValidatePairAsync(libraryId, pair, requireActiveTracks: false, cancellationToken);
        var currentReviews = await reviewRepository.GetAllAsync(cancellationToken);
        if (currentReviews.All(item => item.Pair != pair))
        {
            return CreateLocalResult(pair);
        }

        return await MutateAsync(
            libraryId,
            pair,
            token => duplicateGroupService.DeleteReviewAsync(libraryId, pair, token),
            cancellationToken);
    }

    private async Task<ReviewMutationResult> MutateAsync(
        long libraryId,
        CandidatePairKey pair,
        Func<CancellationToken, Task> mutateCanonicalAsync,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var beforeGroups = await groupRepository.GetByLibraryIdAsync(libraryId, cancellationToken);
        var cleanupTrackIds = GetRelatedTrackIds(beforeGroups, pair);

        // この確認以降にVerdictがCommitされた場合は、呼び出し元のCancelよりDerived Stateの収束を優先する。
        cancellationToken.ThrowIfCancellationRequested();
        var groupKeepStopwatch = Stopwatch.StartNew();
        await mutateCanonicalAsync(cancellationToken);
        groupKeepStopwatch.Stop();

        var afterGroups = await groupRepository.GetByLibraryIdAsync(libraryId, CancellationToken.None);
        var affectedGroups = afterGroups
            .Where(group => group.GlobalTrackIds.Any(cleanupTrackIds.Contains))
            .ToArray();
        var affectedTrackIds = cleanupTrackIds.ToHashSet();
        foreach (var group in affectedGroups)
        {
            affectedTrackIds.UnionWith(group.GlobalTrackIds);
        }

        var supplemental = await supplementalReconciliationService.ReconcileAsync(
            affectedGroups,
            cleanupTrackIds,
            CancellationToken.None);
        var affectedPairKeys = supplemental.AffectedPairKeys.ToHashSet();
        affectedPairKeys.Add(pair);
        totalStopwatch.Stop();

        _logger.LogInformation(
            "Review Mutation完了 LibraryId={LibraryId} Pair={TrackIdA}-{TrackIdB} TotalMs={TotalMs:F1} GroupKeepMs={GroupKeepMs:F1} AffectedTracks={AffectedTrackCount} AffectedPairs={AffectedPairCount}",
            libraryId,
            pair.TrackIdA,
            pair.TrackIdB,
            totalStopwatch.Elapsed.TotalMilliseconds,
            groupKeepStopwatch.Elapsed.TotalMilliseconds,
            affectedTrackIds.Count,
            affectedPairKeys.Count);
        return new ReviewMutationResult(affectedTrackIds, affectedPairKeys, RecoveryPerformed: false);
    }

    private async Task ValidatePairAsync(
        long libraryId,
        CandidatePairKey pair,
        bool requireActiveTracks,
        CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        var a = await trackLookupRepository.GetByIdAsync(pair.TrackIdA, cancellationToken);
        var b = await trackLookupRepository.GetByIdAsync(pair.TrackIdB, cancellationToken);
        if (a is null || b is null)
        {
            throw new InvalidOperationException("レビュー対象のTrackが見つかりません。");
        }

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

    private static HashSet<long> GetRelatedTrackIds(
        IReadOnlyCollection<DuplicateGroup> groups,
        CandidatePairKey pair)
    {
        var result = new HashSet<long> { pair.TrackIdA, pair.TrackIdB };
        foreach (var group in groups.Where(group =>
                     group.GlobalTrackIds.Contains(pair.TrackIdA)
                     || group.GlobalTrackIds.Contains(pair.TrackIdB)))
        {
            result.UnionWith(group.GlobalTrackIds);
        }

        return result;
    }

    private static ReviewMutationResult CreateLocalResult(CandidatePairKey pair)
        => new(
            new HashSet<long> { pair.TrackIdA, pair.TrackIdB },
            new HashSet<CandidatePairKey> { pair },
            RecoveryPerformed: false);
}

/// <summary>
/// Review Mutation後にCandidate Presentationを再評価する範囲を表す。
/// </summary>
public sealed record ReviewMutationResult(
    IReadOnlySet<long> AffectedTrackIds,
    IReadOnlySet<CandidatePairKey> AffectedPairKeys,
    bool RecoveryPerformed);
