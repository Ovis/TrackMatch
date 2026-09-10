using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 保存済みFingerprintから候補ペアを再構築して永続化する。
/// </summary>
public sealed class CandidateGenerationService(
    IFingerprintCatalogRepository fingerprintCatalog,
    ICandidatePairRepository candidatePairRepository,
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
        var result = pairGenerator.Generate(fingerprints, options);
        await candidatePairRepository.ReplaceAllAsync(result.Pairs, cancellationToken);
        return result;
    }
}
