using TrackMatch.Core.Classification;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class RelationshipClassifierTests
{
    private static readonly RelationshipThresholdProfile Profile = new(
        DuplicateMinimumSimilarity: 0.90,
        DuplicateMinimumCoverage: 0.95,
        DuplicateMinimumDurationRatio: 0.95,
        ShortVersionMinimumSimilarity: 0.85,
        ShortVersionMinimumMaximumCoverage: 0.95,
        ShortVersionMaximumMinimumCoverage: 0.60,
        ShortVersionMaximumDurationRatio: 0.60,
        AlternateVersionMinimumSimilarity: 0.65);

    [Fact]
    public void Classify_HighSimilarityAndBothCoverages_ReturnsDuplicateCandidate()
    {
        var result = new RelationshipClassifier(Profile).Classify(
            similarity: 0.95,
            coverageA: 0.98,
            coverageB: 0.97,
            durationRatio: 0.98);

        Assert.Equal(AudioRelationshipKind.DuplicateCandidate, result.Kind);
    }

    [Fact]
    public void Classify_HighSimilarityAndOneSidedCoverage_ReturnsShortVersionCandidate()
    {
        var result = new RelationshipClassifier(Profile).Classify(
            similarity: 0.92,
            coverageA: 0.36,
            coverageB: 0.99,
            durationRatio: 0.37);

        Assert.Equal(AudioRelationshipKind.ShortVersionCandidate, result.Kind);
    }

    [Fact]
    public void Classify_ModerateSimilarity_ReturnsAlternateVersionCandidate()
    {
        var result = new RelationshipClassifier(Profile).Classify(
            similarity: 0.72,
            coverageA: 0.80,
            coverageB: 0.82,
            durationRatio: 0.97);

        Assert.Equal(AudioRelationshipKind.AlternateVersionCandidate, result.Kind);
    }

    [Fact]
    public void Classify_LowSimilarity_ReturnsNeedsReview()
    {
        var result = new RelationshipClassifier(Profile).Classify(
            similarity: 0.42,
            coverageA: 0.90,
            coverageB: 0.91,
            durationRatio: 0.99);

        Assert.Equal(AudioRelationshipKind.NeedsReview, result.Kind);
    }

    [Fact]
    public void Constructor_InvalidProfile_Throws()
    {
        var invalid = Profile with { DuplicateMinimumSimilarity = 1.1 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new RelationshipClassifier(invalid));
    }
}
