using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Persistence;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class CandidateClassificationServiceTests
{
    [Fact]
    public async Task ClassifyAsync_ClassifiesComparisonsAndPersistsProfile()
    {
        var comparisons = new FakeComparisonRepository(
        [
            new CandidateComparison(1, 2, 0.99, 0, TimeSpan.Zero, 100, TimeSpan.FromSeconds(10), 0.98, 0.97, 0.99),
        ]);
        var classifications = new FakeClassificationRepository();
        var service = new CandidateClassificationService(comparisons, classifications);
        var profile = new RelationshipThresholdProfile(
            0.95, 0.95, 0.95,
            0.90, 0.95, 0.60, 0.60,
            0.80);

        await service.ClassifyAsync(profile, TestContext.Current.CancellationToken);

        var saved = Assert.Single(classifications.Saved);
        Assert.Equal(AudioRelationshipKind.DuplicateCandidate, saved.Kind);
        Assert.Contains("DuplicateMinimumSimilarity", saved.ThresholdProfileJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifyAsync_ForPairClassifiesAndReturnsPersistedResult()
    {
        var comparison = new CandidateComparison(
            1, 2, 0.99, 0, TimeSpan.Zero, 100, TimeSpan.FromSeconds(10), 0.98, 0.97, 0.99);
        var classifications = new FakeClassificationRepository();
        var service = new CandidateClassificationService(new FakeComparisonRepository([]), classifications);
        var profile = new RelationshipThresholdProfile(
            0.95, 0.95, 0.95,
            0.90, 0.95, 0.60, 0.60,
            0.80);

        var classification = await service.ClassifyAsync(
            profile,
            comparison,
            TestContext.Current.CancellationToken);

        Assert.Equal(AudioRelationshipKind.DuplicateCandidate, classification.Kind);
        Assert.Same(classification, Assert.Single(classifications.Saved));
    }

    private sealed class FakeComparisonRepository(IReadOnlyList<CandidateComparison> values) : ICandidateComparisonRepository
    {
        public Task ReplaceAllAsync(IReadOnlyCollection<CandidateComparison> comparisons, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyCollection<CandidateComparison> comparisons, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CandidateComparison>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(values);

        public Task<CandidateComparison?> GetAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
            => Task.FromResult(values.SingleOrDefault(item => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB) == pair));

        public Task<IReadOnlyDictionary<CandidatePairKey, DateTime>> GetComparedAtUtcAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<CandidatePairKey, DateTime>>(new Dictionary<CandidatePairKey, DateTime>());
    }

    private sealed class FakeClassificationRepository : ICandidateClassificationRepository
    {
        public IReadOnlyCollection<CandidateClassification> Saved { get; private set; } = [];

        public Task ReplaceAllAsync(IReadOnlyCollection<CandidateClassification> classifications, CancellationToken cancellationToken = default)
        {
            Saved = classifications;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidateClassificationReportRow>> GetReportAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateClassificationReportRow>>([]);

        public Task<CandidateClassification?> GetAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
            => Task.FromResult(Saved.SingleOrDefault(item => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB) == pair));
    }
}
