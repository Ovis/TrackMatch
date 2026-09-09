using TrackMatch.Core.Probe;

namespace TrackMatch.Core.Classification;

/// <summary>
/// Probeで得た音響指標を、校正済みしきい値プロファイルに基づいて分類する。
/// </summary>
public sealed class RelationshipClassifier
{
    private readonly RelationshipThresholdProfile _profile;

    public RelationshipClassifier(RelationshipThresholdProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        _profile = profile;
    }

    public RelationshipClassificationResult Classify(ProbeMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        if (measurement.Similarity >= _profile.DuplicateMinimumSimilarity
            && measurement.MinimumCoverage >= _profile.DuplicateMinimumCoverage
            && measurement.DurationRatio >= _profile.DuplicateMinimumDurationRatio)
        {
            return new RelationshipClassificationResult(
                AudioRelationshipKind.DuplicateCandidate,
                "Similarity・両Coverage・DurationRatioが重複候補のしきい値を満たす。");
        }

        if (measurement.Similarity >= _profile.ShortVersionMinimumSimilarity
            && measurement.MaximumCoverage >= _profile.ShortVersionMinimumMaximumCoverage
            && measurement.MinimumCoverage <= _profile.ShortVersionMaximumMinimumCoverage
            && measurement.DurationRatio <= _profile.ShortVersionMaximumDurationRatio)
        {
            return new RelationshipClassificationResult(
                AudioRelationshipKind.ShortVersionCandidate,
                "短い側をほぼ覆う一方で長い側CoverageとDurationRatioが低く、Short Version候補の条件を満たす。");
        }

        if (measurement.Similarity >= _profile.AlternateVersionMinimumSimilarity)
        {
            return new RelationshipClassificationResult(
                AudioRelationshipKind.AlternateVersionCandidate,
                "一定以上の音響類似度はあるが、重複候補またはShort Version候補の条件を満たさない。");
        }

        return new RelationshipClassificationResult(
            AudioRelationshipKind.NeedsReview,
            "音響類似度が別バージョン候補のしきい値に達していない。");
    }
}
