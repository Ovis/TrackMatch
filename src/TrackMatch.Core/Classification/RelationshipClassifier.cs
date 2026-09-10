using TrackMatch.Core.Probe;

namespace TrackMatch.Core.Classification;

/// <summary>
/// 音響指標を、校正済みしきい値プロファイルに基づいて分類する。
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
        return Classify(measurement.Similarity, measurement.CoverageA, measurement.CoverageB, measurement.DurationRatio);
    }

    public RelationshipClassificationResult Classify(
        double similarity,
        double coverageA,
        double coverageB,
        double durationRatio)
    {
        var minimumCoverage = Math.Min(coverageA, coverageB);
        var maximumCoverage = Math.Max(coverageA, coverageB);

        if (similarity >= _profile.DuplicateMinimumSimilarity
            && minimumCoverage >= _profile.DuplicateMinimumCoverage
            && durationRatio >= _profile.DuplicateMinimumDurationRatio)
        {
            return new RelationshipClassificationResult(
                AudioRelationshipKind.DuplicateCandidate,
                "Similarity・両Coverage・DurationRatioが重複候補のしきい値を満たす。");
        }

        if (similarity >= _profile.ShortVersionMinimumSimilarity
            && maximumCoverage >= _profile.ShortVersionMinimumMaximumCoverage
            && minimumCoverage <= _profile.ShortVersionMaximumMinimumCoverage
            && durationRatio <= _profile.ShortVersionMaximumDurationRatio)
        {
            return new RelationshipClassificationResult(
                AudioRelationshipKind.ShortVersionCandidate,
                "短い側をほぼ覆う一方で長い側CoverageとDurationRatioが低く、Short Version候補の条件を満たす。");
        }

        if (similarity >= _profile.AlternateVersionMinimumSimilarity)
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
