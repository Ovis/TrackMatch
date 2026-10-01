using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.App.Quality;

/// <summary>
/// Review差分で生成されたCandidateへ既存Quality CacheをBatch取得して反映する。
/// </summary>
internal static class CandidateQualityHydrator
{
    /// <summary>
    /// Track / Candidate Qualityを各1回のBatch取得で反映し、不足QualityがあるCandidateを返す。
    /// </summary>
    public static async Task<IReadOnlyList<CandidateReviewItemViewModel>> HydrateAsync(
        IReadOnlyCollection<CandidateReviewItemViewModel> items,
        ITrackQualityAnalysisRepository trackRepository,
        ICandidateQualityComparisonRepository candidateRepository,
        CancellationToken cancellationToken = default,
        Func<CandidateReviewItemViewModel, bool>? shouldApply = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(trackRepository);
        ArgumentNullException.ThrowIfNull(candidateRepository);
        if (items.Count == 0)
        {
            return [];
        }

        var trackIds = items
            .SelectMany(item => new[] { item.TrackIdA, item.TrackIdB })
            .Distinct()
            .ToArray();
        var pairKeys = items
            .Select(item => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB))
            .Distinct()
            .ToArray();
        var analyses = await trackRepository.GetByTrackIdsAsync(trackIds, cancellationToken);
        var comparisons = await candidateRepository.GetByPairsAsync(pairKeys, cancellationToken);
        var missing = new List<CandidateReviewItemViewModel>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shouldApply is not null && !shouldApply(item))
            {
                continue;
            }

            var pair = CandidatePairKey.Create(item.TrackIdA, item.TrackIdB);
            analyses.TryGetValue(item.TrackIdA, out var analysisA);
            analyses.TryGetValue(item.TrackIdB, out var analysisB);
            comparisons.TryGetValue(pair, out var comparison);
            item.ApplyQualityAnalysis(analysisA, analysisB, comparison);
            if (analysisA is null || analysisB is null || comparison is null)
            {
                missing.Add(item);
            }
        }

        return missing;
    }
}
