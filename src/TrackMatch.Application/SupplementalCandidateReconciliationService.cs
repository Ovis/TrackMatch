using System.Diagnostics;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Application;

/// <summary>
/// Reviewで影響したDuplicate Groupだけを対象にSupplemental Candidateを収束させる。
/// </summary>
public sealed class SupplementalCandidateReconciliationService(
    ICandidateReviewRepository reviewRepository,
    ITrackLookupRepository trackLookupRepository,
    ICandidatePairRepository candidatePairRepository,
    CandidateReviewReadyService reviewReadyService,
    Action<SupplementalReconciliationTiming>? timing = null)
{
    /// <summary>
    /// Affected Groupごとに必要な次のReview Pairを最大1件保証し、旧Closure内の不要なSupplementalを削除する。
    /// </summary>
    public async Task<SupplementalReconciliationResult> ReconcileAsync(
        IReadOnlyCollection<DuplicateGroup> affectedGroups,
        IReadOnlyCollection<long> cleanupTrackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affectedGroups);
        ArgumentNullException.ThrowIfNull(cleanupTrackIds);

        var totalStopwatch = Stopwatch.StartNew();
        var supplementalStopwatch = Stopwatch.StartNew();
        var reviews = await GetUsableReviewsAsync(cancellationToken);
        var requiredPairs = new HashSet<CandidatePairKey>();
        var affectedPairKeys = new HashSet<CandidatePairKey>();
        var existingPairKeys = new HashSet<CandidatePairKey>();
        var cleanupIds = cleanupTrackIds.ToHashSet();
        var pairScope = affectedGroups
            .SelectMany(group => group.TrackIds)
            .Concat(cleanupIds)
            .Distinct()
            .ToArray();
        // 起動時は全Groupが対象になる。GroupごとにCandidatePairsを走査せず、
        // 対象TrackのPairを一度だけ取得して各Groupと旧Closureへ振り分ける。
        var scopedPairs = await candidatePairRepository.GetWithinTracksAsync(pairScope, cancellationToken);

        foreach (var group in affectedGroups)
        {
            var groupTrackIds = group.TrackIds.ToHashSet();
            var groupPairs = scopedPairs
                .Where(candidate => groupTrackIds.Contains(candidate.TrackIdA)
                    && groupTrackIds.Contains(candidate.TrackIdB))
                .ToArray();
            foreach (var candidate in groupPairs)
            {
                var pair = CandidatePairKey.Create(candidate.TrackIdA, candidate.TrackIdB);
                existingPairKeys.Add(pair);
                affectedPairKeys.Add(pair);
            }

            if (group.KeepStatus != DuplicateGroupKeepStatus.Unselected)
            {
                continue;
            }

            var globalTrackIds = group.GlobalTrackIds.ToHashSet();
            var groupReviews = reviews
                .Where(review => globalTrackIds.Contains(review.Pair.TrackIdA)
                    && globalTrackIds.Contains(review.Pair.TrackIdB))
                .ToArray();
            if (ReviewNecessityEvaluator.FindSupplementalPair(
                    group.TrackIds,
                    groupReviews,
                    groupPairs) is { } required)
            {
                requiredPairs.Add(required);
                affectedPairKeys.Add(required);
            }
        }

        foreach (var pair in requiredPairs.Where(pair => !existingPairKeys.Contains(pair)))
        {
            await candidatePairRepository.EnsureSupplementalAsync(pair, cancellationToken);
        }

        foreach (var candidate in scopedPairs.Where(candidate => cleanupIds.Contains(candidate.TrackIdA)
                     && cleanupIds.Contains(candidate.TrackIdB)))
        {
            var pair = CandidatePairKey.Create(candidate.TrackIdA, candidate.TrackIdB);
            // Split後にAfter Groupから外れたNormal CandidateもReview Skip状態が変わり得るため、
            // Before Closure内のPairは削除対象でなくてもPresentation再評価範囲へ含める。
            affectedPairKeys.Add(pair);
        }

        await candidatePairRepository.DeleteObsoleteSupplementalWithinTracksAsync(
            cleanupTrackIds,
            requiredPairs,
            cancellationToken);
        supplementalStopwatch.Stop();

        var reviewReadyStopwatch = Stopwatch.StartNew();
        foreach (var pair in requiredPairs)
        {
            await reviewReadyService.EnsureReadyAsync(pair, cancellationToken);
        }
        reviewReadyStopwatch.Stop();
        totalStopwatch.Stop();
        timing?.Invoke(new SupplementalReconciliationTiming(
            affectedGroups.Count,
            requiredPairs.Count,
            supplementalStopwatch.Elapsed,
            reviewReadyStopwatch.Elapsed,
            totalStopwatch.Elapsed));

        return new SupplementalReconciliationResult(affectedPairKeys);
    }

    private async Task<IReadOnlyList<CandidateReview>> GetUsableReviewsAsync(
        CancellationToken cancellationToken)
    {
        var reviews = await reviewRepository.GetAllAsync(cancellationToken);
        var usableByTrackId = new Dictionary<long, bool>();
        var result = new List<CandidateReview>(reviews.Count);
        foreach (var review in reviews)
        {
            if (!usableByTrackId.TryGetValue(review.Pair.TrackIdA, out var usableA))
            {
                usableA = await trackLookupRepository.IsHumanVerdictUsableAsync(
                    review.Pair.TrackIdA,
                    cancellationToken);
                usableByTrackId.Add(review.Pair.TrackIdA, usableA);
            }

            if (!usableByTrackId.TryGetValue(review.Pair.TrackIdB, out var usableB))
            {
                usableB = await trackLookupRepository.IsHumanVerdictUsableAsync(
                    review.Pair.TrackIdB,
                    cancellationToken);
                usableByTrackId.Add(review.Pair.TrackIdB, usableB);
            }

            if (usableA && usableB)
            {
                result.Add(review);
            }
        }

        return result;
    }
}

/// <summary>
/// Supplemental局所整合後にCandidate Presentationを再評価すべきPairを表す。
/// </summary>
public sealed record SupplementalReconciliationResult(
    IReadOnlySet<CandidatePairKey> AffectedPairKeys);

/// <summary>
/// Supplemental局所整合とReview-ready保証の粗い処理時間を表す。
/// </summary>
public sealed record SupplementalReconciliationTiming(
    int AffectedGroupCount,
    int RequiredPairCount,
    TimeSpan SupplementalElapsed,
    TimeSpan ReviewReadyElapsed,
    TimeSpan TotalElapsed);
