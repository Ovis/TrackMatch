using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Application;

/// <summary>
/// 既存Candidate PairのComparisonとClassificationを局所的に補完する。
/// </summary>
public sealed class CandidateReviewReadyService(
    ICandidateComparisonRepository comparisonRepository,
    ICandidateClassificationRepository classificationRepository,
    CandidateAnalysisService analysisService,
    CandidateClassificationService classificationService,
    int fingerprintAlgorithm,
    RelationshipThresholdProfile classificationProfile)
{
    /// <summary>
    /// 指定Pairの既存段階を再利用し、不足しているComparisonとClassificationだけを生成する。
    /// </summary>
    public async Task EnsureReadyAsync(
        CandidatePairKey pair,
        CancellationToken cancellationToken = default)
    {
        if (fingerprintAlgorithm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fingerprintAlgorithm));
        }

        ArgumentNullException.ThrowIfNull(classificationProfile);
        classificationProfile.Validate();

        var comparison = await comparisonRepository.GetAsync(pair, cancellationToken)
            ?? await analysisService.AnalyzeAsync(pair, fingerprintAlgorithm, cancellationToken);
        if (await classificationRepository.GetAsync(pair, cancellationToken) is null)
        {
            await classificationService.ClassifyAsync(
                classificationProfile,
                comparison,
                cancellationToken);
        }
    }
}
