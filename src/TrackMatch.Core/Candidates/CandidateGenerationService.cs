using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 保存済みFingerprintから候補ペアを再構築して永続化する。
/// </summary>
public sealed class CandidateGenerationService(
    IFingerprintCatalogRepository fingerprintCatalog,
    ICandidatePairRepository candidatePairRepository,
    ICandidateReviewRepository reviewRepository,
    CandidatePairGenerator pairGenerator)
{
    public async Task<CandidateGenerationResult> GenerateAsync(
        int fingerprintAlgorithm,
        CandidateGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        if (fingerprintAlgorithm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fingerprintAlgorithm));
        }

        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var fingerprints = await fingerprintCatalog.GetActiveAsync(fingerprintAlgorithm, cancellationToken);
        var generated = pairGenerator.Generate(fingerprints, options);
        var excluded = await reviewRepository.GetExcludedPairKeysAsync(cancellationToken);
        var pairs = generated.Pairs
            .Where(pair => !excluded.Contains(CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB)))
            .ToArray();
        var result = new CandidateGenerationResult(generated.TrackCount, generated.SegmentCount, pairs);
        await candidatePairRepository.ReplaceAllAsync(result.Pairs, cancellationToken);
        return result;
    }
}
