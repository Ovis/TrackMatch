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

        var comparisons = await comparisonRepository.GetAllAsync(cancellationToken);
        await ClassifyAsync(profile, comparisons, cancellationToken);
        return await classificationRepository.GetReportAsync(cancellationToken);
    }

    /// <summary>
    /// 指定した詳細比較結果だけを分類し、既存の他Pairの分類を維持したまま永続化する。
    /// </summary>
    /// <param name="profile">分類に使用するしきい値Profile</param>
    /// <param name="comparisons">分類対象の詳細比較結果</param>
    /// <param name="cancellationToken">処理のキャンセル要求</param>
    public async Task ClassifyAsync(
        RelationshipThresholdProfile profile,
        IReadOnlyCollection<CandidateComparison> comparisons,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(comparisons);
        profile.Validate();

        if (comparisons.Count == 0)
        {
            return;
        }

        var classifier = new RelationshipClassifier(profile);
        var profileJson = JsonSerializer.Serialize(profile);
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
    }
}
