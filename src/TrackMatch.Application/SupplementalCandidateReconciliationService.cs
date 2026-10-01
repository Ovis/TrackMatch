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
        var reviewedPairs = reviews.Select(review => review.Pair).ToHashSet();
        var requiredPairs = new HashSet<CandidatePairKey>();
        var affectedPairKeys = new HashSet<CandidatePairKey>();
        var existingPairKeys = new HashSet<CandidatePairKey>();

        foreach (var group in affectedGroups)
        {
            var groupPairs = await candidatePairRepository.GetWithinTracksAsync(
                group.TrackIds,
                cancellationToken);
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

        var cleanupPairs = await candidatePairRepository.GetWithinTracksAsync(
            cleanupTrackIds,
            cancellationToken);
        foreach (var candidate in cleanupPairs.Where(candidate => candidate.MinimumSegmentHashDistance < 0))
        {
            var pair = CandidatePairKey.Create(candidate.TrackIdA, candidate.TrackIdB);
            if (!reviewedPairs.Contains(pair) && !requiredPairs.Contains(pair))
            {
                // Delete APIは件数を返さないため、削除前にPresentationから除くべきPairを記録する。
                affectedPairKeys.Add(pair);
            }
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
