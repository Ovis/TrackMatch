using System.Text.Json;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 詳細比較済み候補へ関係分類を適用し、結果を永続化する。
/// </summary>
public sealed class CandidateClassificationService(
    ICandidateComparisonRepository comparisonRepository,
    ICandidateClassificationRepository classificationRepository)
{
    public async Task<IReadOnlyList<CandidateClassificationReportRow>> ClassifyAsync(
        RelationshipThresholdProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        var classifier = new RelationshipClassifier(profile);
        var profileJson = JsonSerializer.Serialize(profile);
        var comparisons = await comparisonRepository.GetAllAsync(cancellationToken);
        var classifications = comparisons
            .Select(comparison =>
            {
                var result = classifier.Classify(
                    comparison.Similarity,
                    comparison.CoverageA,
                    comparison.CoverageB,
                    comparison.DurationRatio);
                return new CandidateClassification(
                    comparison.TrackIdA,
                    comparison.TrackIdB,
                    result.Kind,
                    result.Reason,
                    profileJson);
            })
            .ToArray();

        await classificationRepository.ReplaceAllAsync(classifications, cancellationToken);
        return await classificationRepository.GetReportAsync(cancellationToken);
    }
}
