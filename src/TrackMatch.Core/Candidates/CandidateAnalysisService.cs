using TrackMatch.Core.Comparison;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 保存済み候補ペアをraw Fingerprintで詳細比較し、測定値を永続化する。
/// </summary>
public sealed class CandidateAnalysisService(
    IFingerprintCatalogRepository fingerprintCatalog,
    ICandidatePairRepository candidatePairRepository,
    ICandidateComparisonRepository comparisonRepository,
    FingerprintComparer comparer)
{
    public async Task<CandidateAnalysisResult> AnalyzeAsync(
        int fingerprintAlgorithm,
        CancellationToken cancellationToken = default)
    {
        if (fingerprintAlgorithm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fingerprintAlgorithm));
        }

        var pairs = await candidatePairRepository.GetAllAsync(cancellationToken);
        var fingerprints = await fingerprintCatalog.GetActiveAsync(fingerprintAlgorithm, cancellationToken);
        var fingerprintsByTrackId = fingerprints.ToDictionary(item => item.TrackId);
        var comparisons = new List<CandidateComparison>(pairs.Count);
        var skipped = 0;

        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fingerprintsByTrackId.TryGetValue(pair.TrackIdA, out var a) ||
                !fingerprintsByTrackId.TryGetValue(pair.TrackIdB, out var b))
            {
                // Track更新後などで候補生成時のFingerprintが失効している場合は、古い組み合わせを比較しない。
                skipped++;
                continue;
            }

            var result = comparer.Compare(a.Fingerprint, b.Fingerprint);
            comparisons.Add(new CandidateComparison(
                pair.TrackIdA,
                pair.TrackIdB,
                result.Similarity,
                result.BestOffsetItems,
                result.BestOffset,
                result.MatchedItems,
                result.MatchedDuration,
                result.CoverageA,
                result.CoverageB,
                CalculateDurationRatio(a.Fingerprint.Duration, b.Fingerprint.Duration)));
        }

        await comparisonRepository.ReplaceAllAsync(comparisons, cancellationToken);
        return new CandidateAnalysisResult(pairs.Count, comparisons.Count, skipped);
    }

    private static double CalculateDurationRatio(TimeSpan a, TimeSpan b)
    {
        var maximum = Math.Max(a.TotalSeconds, b.TotalSeconds);
        return maximum <= 0d ? 0d : Math.Min(a.TotalSeconds, b.TotalSeconds) / maximum;
    }
}
