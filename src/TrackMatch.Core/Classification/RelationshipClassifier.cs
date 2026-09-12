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
                "音響一致度、一致範囲、再生時間の近さが、重複と判断する条件を満たしています。");
        }

        if (similarity >= _profile.ShortVersionMinimumSimilarity
            && maximumCoverage >= _profile.ShortVersionMinimumMaximumCoverage
            && minimumCoverage <= _profile.ShortVersionMaximumMinimumCoverage
            && durationRatio <= _profile.ShortVersionMaximumDurationRatio)
        {
            return new RelationshipClassificationResult(
                AudioRelationshipKind.ShortVersionCandidate,
                "片方の音源の大部分が一致していますが、もう片方は一致範囲が狭く再生時間も異なるため、短縮版の候補です。");
        }

        if (similarity >= _profile.AlternateVersionMinimumSimilarity)
        {
            return new RelationshipClassificationResult(
                AudioRelationshipKind.AlternateVersionCandidate,
                "音響的には似ていますが、重複または短縮版と判断する条件には当てはまりません。");
        }

        return new RelationshipClassificationResult(
            AudioRelationshipKind.NeedsReview,
            "音響一致度が、別バージョン候補として自動判定する基準に達していません。");
    }
}
